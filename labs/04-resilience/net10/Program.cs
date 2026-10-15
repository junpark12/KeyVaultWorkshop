using System;
using System.Threading.Tasks;
using Azure;
using Azure.Identity;

namespace KeyVaultResilience
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            try
            {
                if (args.Length != 1)
                    throw new ArgumentException("Specify probe or load-test.");

                switch (args[0])
                {
                    case "probe":
                        return await KeyVaultLoadRunner.RunAsync(probeOnly: true);
                    case "load-test":
                        return await KeyVaultLoadRunner.RunAsync(probeOnly: false);
                    default:
                        throw new ArgumentException("Unknown command. Use probe or load-test.");
                }
            }
            catch (CredentialUnavailableException)
            {
                Console.Error.WriteLine(
                    "AUTH_UNAVAILABLE: Check AZURE_TENANT_ID, AZURE_CLIENT_ID, and AZURE_CLIENT_SECRET.");
                return 2;
            }
            catch (AuthenticationFailedException)
            {
                Console.Error.WriteLine(
                    "AUTH_FAILED: Check the SP tenant, client ID, secret VALUE/expiry, and tenant policy.");
                return 2;
            }
            catch (RequestFailedException ex)
            {
                Console.Error.WriteLine(
                    "KEY_VAULT_ERROR status=" + ex.Status + " code=" + ex.ErrorCode);
                return 2;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("TIMEOUT: Operation budget exceeded or operation cancelled.");
                return 3;
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("CONFIG_ERROR: " + ex.Message);
                return 1;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine("OPERATION_ERROR: " + ex.Message);
                return 1;
            }
        }
    }
}
