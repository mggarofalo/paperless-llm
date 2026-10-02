using PaperlessLlm.Auth;
using PaperlessLlm.Inference;
using PaperlessLlm.Paperless;


namespace PaperlessLlm;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try
        {
            return await OrganizerCli.RunAsync(args, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 130; }
        catch (Exception ex) when (ex is AuthException or InferenceException or PaperlessException or ArgumentException or Intent.OrganizationPromptException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception)
        {
            // Untrusted provider responses, documents and credential paths must never reach stdout.
            Console.Error.WriteLine("Operation failed. Check configuration, volume access and service connectivity.");
            return 1;
        }
    }

}
