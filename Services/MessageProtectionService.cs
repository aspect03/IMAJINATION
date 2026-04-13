using Microsoft.AspNetCore.DataProtection;

namespace ImajinationAPI.Services
{
    public class MessageProtectionService
    {
        private const string Prefix = "enc:v1:";
        private readonly IDataProtector _protector;

        public MessageProtectionService(IDataProtectionProvider provider)
        {
            _protector = provider.CreateProtector("Imajination.Messages.AtRest.v1");
        }

        public string Protect(string? plaintext)
        {
            if (string.IsNullOrWhiteSpace(plaintext))
            {
                return string.Empty;
            }

            if (plaintext.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return plaintext;
            }

            return Prefix + _protector.Protect(plaintext);
        }

        public string Unprotect(string? protectedValue)
        {
            if (string.IsNullOrWhiteSpace(protectedValue))
            {
                return string.Empty;
            }

            if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return protectedValue;
            }

            try
            {
                return _protector.Unprotect(protectedValue[Prefix.Length..]);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
