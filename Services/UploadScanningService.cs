using System.Net.Sockets;
using System.Text;

namespace ImajinationAPI.Services
{
    public sealed class UploadScanningService
    {
        private readonly IConfiguration _configuration;

        public UploadScanningService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public async Task<(bool IsClean, string? Message)> ScanDataUrlAsync(string? dataUrl, string fileLabel = "upload")
        {
            if (string.IsNullOrWhiteSpace(dataUrl))
            {
                return (true, null);
            }

            var commaIndex = dataUrl.IndexOf(',');
            if (commaIndex <= 5)
            {
                return (false, "Upload scanning failed because the payload is invalid.");
            }

            var meta = dataUrl[..commaIndex];
            var payload = dataUrl[(commaIndex + 1)..];
            var mime = meta[5..].Split(';', StringSplitOptions.RemoveEmptyEntries)[0].Trim().ToLowerInvariant();

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(payload);
            }
            catch
            {
                return (false, "Upload scanning failed because the payload is invalid.");
            }

            var signatureResult = ValidateImageMagic(mime, bytes);
            if (!signatureResult.IsClean)
            {
                return signatureResult;
            }

            var heuristicResult = ScanWithHeuristics(bytes, fileLabel);
            if (!heuristicResult.IsClean)
            {
                return heuristicResult;
            }

            var clamHost = _configuration["Security:ClamAvHost"];
            if (!string.IsNullOrWhiteSpace(clamHost))
            {
                var clamPort = int.TryParse(_configuration["Security:ClamAvPort"], out var configuredPort) ? configuredPort : 3310;
                var clamResult = await ScanWithClamAvAsync(clamHost, clamPort, bytes);
                if (!clamResult.IsClean)
                {
                    return clamResult;
                }
            }

            return (true, null);
        }

        private static (bool IsClean, string? Message) ValidateImageMagic(string mime, byte[] bytes)
        {
            bool matches = mime switch
            {
                "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
                "image/png" => bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47,
                "image/gif" => bytes.Length >= 6 && Encoding.ASCII.GetString(bytes, 0, 6) is "GIF87a" or "GIF89a",
                "image/webp" => bytes.Length >= 12
                    && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF"
                    && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP",
                _ => false
            };

            return matches
                ? (true, null)
                : (false, "The uploaded image content does not match the declared file type.");
        }

        private static (bool IsClean, string? Message) ScanWithHeuristics(byte[] bytes, string fileLabel)
        {
            var ascii = Encoding.ASCII.GetString(bytes);
            if (ascii.Contains("X5O!P%@AP[4\\PZX54(P^)7CC)7}$", StringComparison.Ordinal))
            {
                return (false, $"The {fileLabel} matched a malware test signature and was blocked.");
            }

            if (bytes.Length >= 2 && bytes[0] == 0x4D && bytes[1] == 0x5A)
            {
                return (false, $"The {fileLabel} appears to contain executable content and was blocked.");
            }

            if (ascii.Contains("<script", StringComparison.OrdinalIgnoreCase) ||
                ascii.Contains("powershell", StringComparison.OrdinalIgnoreCase) ||
                ascii.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"The {fileLabel} contains suspicious executable content and was blocked.");
            }

            return (true, null);
        }

        private static async Task<(bool IsClean, string? Message)> ScanWithClamAvAsync(string host, int port, byte[] bytes)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port);
                await using var stream = client.GetStream();

                var command = Encoding.ASCII.GetBytes("zINSTREAM\0");
                await stream.WriteAsync(command);

                var lengthPrefix = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(bytes.Length));
                await stream.WriteAsync(lengthPrefix);
                await stream.WriteAsync(bytes);
                await stream.WriteAsync(new byte[] { 0, 0, 0, 0 });
                await stream.FlushAsync();

                var buffer = new byte[2048];
                var read = await stream.ReadAsync(buffer);
                var response = Encoding.ASCII.GetString(buffer, 0, read);
                if (response.Contains("FOUND", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "The upload was blocked by malware scanning.");
                }

                return (true, null);
            }
            catch
            {
                return (false, "The upload could not be scanned by the malware engine.");
            }
        }
    }
}
