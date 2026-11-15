using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OperationRecorderApp
{
    enum TranscriptionProvider { None, Whisper, Haas, Compatible, OpenAI, Gemini, Windows }

    sealed record TranscriptionSettings(TranscriptionProvider Provider, string Python, string LocalModel,
        string Model, Uri Endpoint)
    {
        public static TranscriptionProvider SelectedProvider(AppSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.NarrationProvider))
                return !string.IsNullOrWhiteSpace(settings.TranscriptionPython) &&
                    !string.IsNullOrWhiteSpace(settings.TranscriptionModel) ? TranscriptionProvider.Whisper : TranscriptionProvider.None;
            return Enum.TryParse(settings.NarrationProvider, ignoreCase: true, out TranscriptionProvider provider) &&
                Enum.IsDefined(provider) ? provider : TranscriptionProvider.None;
        }

        public static TranscriptionSettings From(AppSettings settings) => From(settings, SelectedProvider(settings));

        public static TranscriptionSettings From(AppSettings settings, TranscriptionProvider provider)
        {
            string python = provider == TranscriptionProvider.Haas ? settings.HaasPython : settings.TranscriptionPython;
            string folder = provider == TranscriptionProvider.Haas ? settings.HaasModel : settings.TranscriptionModel;
            return new TranscriptionSettings(provider, python?.Trim(), folder?.Trim(),
                provider switch
                {
                    TranscriptionProvider.Compatible => Fallback(settings.TranscriptionServerModel, null),
                    TranscriptionProvider.OpenAI => Fallback(settings.TranscriptionOpenAIModel, "gpt-4o-transcribe-diarize"),
                    TranscriptionProvider.Gemini => Fallback(settings.TranscriptionGeminiModel, "gemini-3.5-flash-lite"),
                    _ => null,
                }, provider == TranscriptionProvider.Compatible ? ServerEndpoint(settings.TranscriptionServerUrl) : null);
        }

        static string Fallback(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        public static Uri ServerEndpoint(string address)
        {
            if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) ||
                !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("互換サーバーにはHTTPSまたはこのPCのループバックHTTPのURLを指定してください。");
            string path = uri.AbsolutePath.TrimEnd('/');
            if (path.Length == 0) path = "/v1/audio/transcriptions";
            else if (path.EndsWith("/v1", StringComparison.Ordinal)) path += "/audio/transcriptions";
            return new UriBuilder(uri) { Path = path }.Uri;
        }
    }

    sealed class TranscriptionCredentials(string settingsDirectory)
    {
        string KeyPath(TranscriptionProvider provider, Uri endpoint)
        {
            string name = provider switch
            {
                TranscriptionProvider.OpenAI => "openai",
                TranscriptionProvider.Gemini => "gemini",
                TranscriptionProvider.Compatible when endpoint != null => "server-" +
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.AbsoluteUri))),
                _ => throw new ArgumentException("この文字起こし方式ではAPIキーを使用しません。"),
            };
            return Path.Combine(settingsDirectory, "credentials", name + ".dpapi");
        }

        public bool Exists(TranscriptionProvider provider, Uri endpoint) => File.Exists(KeyPath(provider, endpoint));

        public void Remove(TranscriptionProvider provider, Uri endpoint) => File.Delete(KeyPath(provider, endpoint));

        public string Read(TranscriptionProvider provider, Uri endpoint)
        {
            string path = KeyPath(provider, endpoint);
            try
            {
                if (!File.Exists(path)) return null;
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                try { return Encoding.UTF8.GetString(plain); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            catch (Exception error) when (error is CryptographicException || error is IOException || error is UnauthorizedAccessException)
            {
                throw new InvalidOperationException("保存済みのAPIキーを読み込めません。「記録対象」でキーを保存し直してください。");
            }
        }

        public void Save(TranscriptionProvider provider, Uri endpoint, string key)
        {
            key = key?.Trim();
            if (string.IsNullOrEmpty(key) || key.Length > 2048 || System.Linq.Enumerable.Any(key, c => c < '!' || c > '~'))
                throw new ArgumentException("有効なAPIキーを入力してください。");
            string path = KeyPath(provider, endpoint);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            byte[] plain = Encoding.UTF8.GetBytes(key);
            try
            {
                File.WriteAllBytes(temporary, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
                File.Delete(temporary);
            }
        }
    }
}
