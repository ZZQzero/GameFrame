using System.Security.Cryptography;
using System.Text;

namespace Game.Server.Login
{
    internal static class PasswordHasher
    {
        internal const int Iterations = 600000;

        internal static bool Valid(string account, string password) =>
            !string.IsNullOrWhiteSpace(account) && account.Length <= 64 && account == account.Trim() &&
            !account.Any(char.IsControl) && password.Length >= 8 && password.Length <= 128;

        internal static byte[] Hash(string password, byte[] salt, int iterations)
        {
            if (salt.Length != 16 || iterations != Iterations)
            {
                throw new InvalidOperationException("Unsupported stored password format.");
            }

            byte[] bytes = Encoding.UTF8.GetBytes(password);
            try
            {
                return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, HashAlgorithmName.SHA256, 32);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }
}
