# Authentication

[Home](../README.md) · [Operations](operations.md) · [Accuracy reports](review.md)

The worker needs two independent credentials: a Paperless token to read documents,
and a ChatGPT grant to request model reviews. Neither belongs in the Compose file,
`.env`, source control, or logs.

## 1. Create a view-only Paperless account

In Paperless, open **Settings → Users & Groups** and create a dedicated account
such as `paperless-llm`. Leave superuser and admin/staff status disabled. Grant
only **View** for Documents, Tags, Correspondents and Document Types. If the
account needs the web interface, also grant the UI Settings view permission.
Do not grant add, change or delete permissions.

Global view permission does not automatically expose documents owned by another
user. Give this account (or a dedicated group) object-level **view** access to the
documents and metadata you want it to inspect. Ensure future documents receive
the same view access through your normal Paperless permissions/workflow setup.
The worker cannot change those permissions itself. See the official
[Paperless permissions guide](https://docs.paperless-ngx.com/usage/#permissions).

Sign in as this dedicated account and obtain its API token from its profile.
Paperless also supports requesting a token through `/api/token/`; see its
[authentication documentation](https://docs.paperless-ngx.com/api/#authorization).
Save only the token value in `secrets/paperless_token.txt`, with no quotes or
`Token` prefix. Create the `secrets` directory first. Compose mounts this file
read-only into the worker.

On a Linux Docker host, the image runs as UID 1654. Make the token readable by that
UID while protecting the deployment directory from other users. For example,
after creating the file:

```sh
chmod 700 secrets
sudo chown 1654:1654 secrets/paperless_token.txt
sudo chmod 400 secrets/paperless_token.txt
```

Rootless Docker and user-namespace remapping may need different host ownership.
Keep your existing personal/admin token out of this deployment.

## 2. Sign in with ChatGPT

From the deployment directory:

```sh
docker compose pull
docker compose --profile setup run --rm --service-ports auth
```

Open the printed authorization URL in your browser. Select the account/workspace
whose plan you want to use and authorize **Paperless LLM**. The callback returns
to port 1455 on your computer. Wait for the terminal to confirm success.

The temporary container exits after sign-in. Credentials stay in the `auth`
Docker volume. Do not use `docker compose down -v`, which deletes that volume.
Check the grant and available models:

```sh
docker compose --profile setup run --rm auth auth status
docker compose --profile setup run --rm auth models
docker compose --profile setup run --rm auth probe
```

Set `PPLLM_MODEL` in `.env` to an available model slug. The sample uses
`gpt-6-luna`; you can select `gpt-6-sol` if your grant exposes it.
The optional `probe` sends synthetic text only and verifies a completed model
response without reading any Paperless documents.

### Signing in on a remote Docker server

The browser callback goes to the browser's computer, so forward its localhost
port to your server. From your laptop, open a terminal and keep this running:

```sh
ssh -N -o ExitOnForwardFailure=yes -L 127.0.0.1:1455:127.0.0.1:1455 your-user@your-docker-server
```

In a second terminal, SSH to the server, enter the deployment directory, and run
the sign-in command above. Open its authorization URL in your laptop browser.
The tunnel carries the callback to the temporary auth container. After sign-in,
close the tunnel. No public callback port or reverse-proxy route is needed.

The temporary auth container listens on all of its **container** interfaces so
Docker can deliver the forwarded connection. Compose publishes it only on the
server's loopback address. The authorization callback always remains
`http://127.0.0.1:1455/auth/callback`; do not replace it with your server's public
hostname or expose port 1455 publicly.

Only one process may use a credential volume at a time. Stop the worker before
signing in again. Do not copy the same live refresh token into multiple workers.

## 3. Start the worker

```sh
docker compose run --rm worker check
docker compose up -d worker
docker compose logs -f worker
```

`check` verifies both connections and the configured model and review tag without
enrolling jobs or sending documents to the model. If it reports no visible
documents when your library is not empty, fix Paperless object permissions before
starting the worker.

The app stores the issued client registration and a stable host identifier, and
renews its access token using its saved refresh token. Restarting the container
does not normally require another sign-in. Tokens rotate and are saved atomically.
This follows OpenAI's [session and refresh documentation](https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions).

“One-time” means initial enrollment, not permanent authorization. Revocation,
workspace-policy changes or a sufficiently long shutdown can require sign-in
again. The worker reports an authentication failure and waits instead of switching
to API-key billing. Stop it and repeat step 2 to recover.

### Verification status

The OAuth, signed-token validation, rotating refresh and inference-stream paths
have synthetic automated tests. A live dedicated Paperless LLM grant has not yet
been verified for this release. Complete `models` and `probe` above on your
deployment before starting document reviews. These require your account's grant
and are intentionally not CI prerequisites. A successful sign-in alone does not
prove that your selected model is available.

## Sign out or revoke access

Stop the worker before changing its authorization:

```sh
docker compose stop worker
docker compose --profile setup run --rm auth auth logout
```

The command attempts remote session revocation before clearing local tokens. If
it reports that remote revocation was not confirmed, disconnect **Paperless LLM**
in ChatGPT Settings as well. Its saved client registration and host identifier
remain available for a later sign-in.

You can also revoke the app's access in your ChatGPT account. Revoke the dedicated
Paperless token separately if retiring the deployment. Treat backups of the
`auth` volume as credentials: encrypt them and restrict access.
