using System.Security.Cryptography;
using System.Text.Json;

namespace KLaniBot.Backup;

// Public backup entry points report expected filesystem/crypto failures without echoing private paths or decrypted contents.
internal static class BackupAttempt
{
    public static bool Try<T>(Func<T> run, out T? value, out BackupFailure failure) where T : class
    {
        value = null;
        failure = BackupFailure.None;
        try
        {
            value = run();
            return true;
        }
        catch (BackupFault exception)
        {
            failure = exception.Failure;
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException)
        {
            failure = BackupFailure.InvalidArchive;
        }
        catch (Exception)
        {
            failure = BackupFailure.StoreFailed;
        }

        return false;
    }
}
