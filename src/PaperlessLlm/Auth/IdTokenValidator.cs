using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace PaperlessLlm.Auth;

public sealed class IdTokenValidator(HttpClient http)
{
    public async Task<string> ValidateAsync(string token, string clientId, string? nonce,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.GetAsync("https://auth.openai.com/.well-known/jwks.json", cancellationToken);
            if (!response.IsSuccessStatusCode) throw new AuthException("OpenAI signing keys are unavailable.");
            var keys = new JsonWebKeySet(await response.Content.ReadAsStringAsync(cancellationToken));
            var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidIssuer = "https://auth.openai.com", ValidAudience = clientId,
                IssuerSigningKeys = keys.GetSigningKeys(), ValidateIssuerSigningKey = true,
                ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
                RequireSignedTokens = true, RequireExpirationTime = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ClockSkew = TimeSpan.FromSeconds(30)
            });
            if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
                throw new AuthException("The sign-in identity could not be verified.");
            if (nonce is not null && (!jwt.TryGetClaim("nonce", out var claim) || !FixedEquals(claim.Value, nonce)))
                throw new AuthException("The sign-in identity could not be verified.");
            if (string.IsNullOrWhiteSpace(jwt.Subject)) throw new AuthException("The sign-in identity could not be verified.");
            return jwt.Subject;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException or SecurityTokenException)
        {
            throw new AuthException("The sign-in identity could not be verified.");
        }
    }

    internal static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
}
