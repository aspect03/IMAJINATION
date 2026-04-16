using System.Security.Cryptography;
using System.Text;

namespace ImajinationAPI.Services
{
    public sealed class TotpService
    {
        private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        public string GenerateSecret(int byteLength = 20)
        {
            var bytes = RandomNumberGenerator.GetBytes(Math.Max(20, byteLength));
            return Base32Encode(bytes);
        }

        public string BuildManualEntryKey(string base32Secret)
        {
            var normalized = NormalizeBase32(base32Secret);
            var groups = Enumerable.Range(0, (int)Math.Ceiling(normalized.Length / 4d))
                .Select(index => normalized.Skip(index * 4).Take(4))
                .Where(group => group.Any())
                .Select(chars => new string(chars.ToArray()));
            return string.Join(' ', groups);
        }

        public string BuildOtpAuthUri(string issuer, string accountLabel, string base32Secret)
        {
            var safeIssuer = Uri.EscapeDataString(issuer);
            var safeLabel = Uri.EscapeDataString($"{issuer}:{accountLabel}");
            var secret = Uri.EscapeDataString(NormalizeBase32(base32Secret));
            return $"otpauth://totp/{safeLabel}?secret={secret}&issuer={safeIssuer}&algorithm=SHA1&digits=6&period=30";
        }

        public bool ValidateCode(string? base32Secret, string? code, int allowedTimeStepDrift = 1)
        {
            if (string.IsNullOrWhiteSpace(base32Secret) || string.IsNullOrWhiteSpace(code))
            {
                return false;
            }

            var normalizedCode = new string(code.Where(char.IsDigit).ToArray());
            if (normalizedCode.Length != 6)
            {
                return false;
            }

            var secretBytes = Base32Decode(base32Secret);
            var currentCounter = GetCurrentCounter();
            for (var offset = -allowedTimeStepDrift; offset <= allowedTimeStepDrift; offset++)
            {
                var expected = ComputeTotp(secretBytes, currentCounter + offset);
                if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected),
                    Encoding.ASCII.GetBytes(normalizedCode)))
                {
                    return true;
                }
            }

            return false;
        }

        private static long GetCurrentCounter()
        {
            var unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return unixTime / 30;
        }

        private static string ComputeTotp(byte[] secret, long counter)
        {
            var counterBytes = BitConverter.GetBytes(counter);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(counterBytes);
            }

            using var hmac = new HMACSHA1(secret);
            var hash = hmac.ComputeHash(counterBytes);
            var offset = hash[^1] & 0x0F;
            var binaryCode =
                ((hash[offset] & 0x7F) << 24) |
                ((hash[offset + 1] & 0xFF) << 16) |
                ((hash[offset + 2] & 0xFF) << 8) |
                (hash[offset + 3] & 0xFF);

            var otp = binaryCode % 1_000_000;
            return otp.ToString("D6");
        }

        private static string NormalizeBase32(string? value)
        {
            return new string((value ?? string.Empty)
                .ToUpperInvariant()
                .Where(ch => Base32Alphabet.Contains(ch))
                .ToArray());
        }

        private static byte[] Base32Decode(string value)
        {
            var normalized = NormalizeBase32(value);
            var output = new List<byte>();
            var bits = 0;
            var valueBuffer = 0;

            foreach (var ch in normalized)
            {
                var charValue = Base32Alphabet.IndexOf(ch);
                if (charValue < 0)
                {
                    continue;
                }

                valueBuffer = (valueBuffer << 5) | charValue;
                bits += 5;
                if (bits >= 8)
                {
                    output.Add((byte)((valueBuffer >> (bits - 8)) & 0xFF));
                    bits -= 8;
                }
            }

            return output.ToArray();
        }

        private static string Base32Encode(byte[] bytes)
        {
            var output = new StringBuilder();
            var bits = 0;
            var value = 0;

            foreach (var b in bytes)
            {
                value = (value << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    output.Append(Base32Alphabet[(value >> (bits - 5)) & 0x1F]);
                    bits -= 5;
                }
            }

            if (bits > 0)
            {
                output.Append(Base32Alphabet[(value << (5 - bits)) & 0x1F]);
            }

            return output.ToString();
        }
    }
}
