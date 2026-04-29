using System.Security.Cryptography;
using System.Text;

namespace ImajinationAPI.Services
{
    public sealed record AutomatedVerificationAssessment(
        string Status,
        string Recommendation,
        int Score,
        string Notes);

    public sealed class AutomatedVerificationAssessmentService
    {
        public AutomatedVerificationAssessment Assess(
            string idType,
            string idNumberLast4,
            string evidenceSummary,
            string? idImageFront,
            string? idImageBack,
            string? selfieImage)
        {
            var score = 100;
            var notes = new List<string>();

            if (string.IsNullOrWhiteSpace(idType))
            {
                score -= 20;
                notes.Add("ID type is missing.");
            }

            if (string.IsNullOrWhiteSpace(idNumberLast4) || idNumberLast4.Trim().Length < 4)
            {
                score -= 20;
                notes.Add("Last 4 ID characters are incomplete.");
            }

            if (string.IsNullOrWhiteSpace(evidenceSummary) || evidenceSummary.Trim().Length < 20)
            {
                score -= 10;
                notes.Add("Evidence summary is too short for reliable screening context.");
            }

            var frontMeta = AnalyzeImage(idImageFront, "ID front", required: true);
            var backMeta = AnalyzeImage(idImageBack, "ID back", required: false);
            var selfieMeta = AnalyzeImage(selfieImage, "Selfie", required: true);

            score -= frontMeta.Penalty + backMeta.Penalty + selfieMeta.Penalty;
            notes.AddRange(frontMeta.Notes);
            notes.AddRange(backMeta.Notes);
            notes.AddRange(selfieMeta.Notes);

            if (!string.IsNullOrWhiteSpace(frontMeta.Hash) &&
                frontMeta.Hash == backMeta.Hash)
            {
                score -= 20;
                notes.Add("ID front and ID back appear to be the same image.");
            }

            if (!string.IsNullOrWhiteSpace(frontMeta.Hash) &&
                frontMeta.Hash == selfieMeta.Hash)
            {
                score -= 25;
                notes.Add("ID front and selfie appear to be the same image.");
            }

            if (!string.IsNullOrWhiteSpace(backMeta.Hash) &&
                backMeta.Hash == selfieMeta.Hash)
            {
                score -= 25;
                notes.Add("ID back and selfie appear to be the same image.");
            }

            score = Math.Clamp(score, 0, 100);

            var recommendation = score >= 85 && notes.All(note => !note.Contains("same image", StringComparison.OrdinalIgnoreCase))
                ? "Ready for Admin Review"
                : score >= 60
                    ? "Review Carefully"
                    : "Needs Manual Attention";

            var status = "Auto-Screened";
            if (!notes.Any())
            {
                notes.Add("All uploaded verification assets passed automated pre-checks.");
            }

            return new AutomatedVerificationAssessment(
                status,
                recommendation,
                score,
                string.Join(" ", notes));
        }

        private static ImageAssessment AnalyzeImage(string? dataUrl, string label, bool required)
        {
            if (string.IsNullOrWhiteSpace(dataUrl))
            {
                return required
                    ? new ImageAssessment(30, null, [$"{label} is missing."])
                    : new ImageAssessment(8, null, [$"{label} was not provided."]);
            }

            var commaIndex = dataUrl.IndexOf(',');
            if (commaIndex <= 5)
            {
                return new ImageAssessment(25, null, [$"{label} payload could not be parsed."]);
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
                return new ImageAssessment(25, null, [$"{label} is not valid base64 image data."]);
            }

            var notes = new List<string>();
            var penalty = 0;

            if (bytes.Length < 40_000)
            {
                penalty += 12;
                notes.Add($"{label} may be too low-resolution for reliable review.");
            }

            if (bytes.Length > 3_200_000)
            {
                penalty += 6;
                notes.Add($"{label} is unusually large and may need compression.");
            }

            if (mime is not ("image/jpeg" or "image/png" or "image/webp"))
            {
                penalty += 10;
                notes.Add($"{label} uses a less common image format.");
            }

            return new ImageAssessment(penalty, ComputeHash(bytes), notes);
        }

        private static string ComputeHash(byte[] bytes)
        {
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }

        private sealed record ImageAssessment(int Penalty, string? Hash, List<string> Notes);
    }
}
