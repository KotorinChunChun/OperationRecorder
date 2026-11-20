using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OperationRecorderApp
{
    static class TranscriptionServerConnection
    {
        public static async Task<(string Key, string Model)> Prepare(HttpClient client, Uri endpoint,
            string savedKey, string requestedModel, CancellationToken cancellation)
        {
            var healthAddress = new Uri(endpoint, "/api/health");
            var health = await ReadStatus(client, healthAddress, null, "状態確認", allowUnauthorized: true, cancellation).ConfigureAwait(false);
            if (health == null)
            {
                if (savedKey == null) throw new IOException("TranscriptionServerのBearer認証用のキーを「記録対象」で保存してください。");
                health = await ReadStatus(client, healthAddress, savedKey, "状態確認", allowUnauthorized: false, cancellation).ConfigureAwait(false);
            }

            if ((string)health["appId"] != "meeting-transcriber" || (int?)health["protocolVersion"] != 3 ||
                string.IsNullOrWhiteSpace((string)health["serverVersion"]) || (bool?)health["capabilities"]?["openaiAudio"] != true)
                throw new InvalidDataException("接続先は対応するTranscriptionServerではありません。protocolVersion=3 の認識APIが必要です。");
            if ((bool?)health["ready"] != true || (bool?)health["acceptingJobs"] != true)
                throw new IOException("TranscriptionServerは現在受付できません。サーバーの実行環境、モデルの準備状況、受付状態を確認してください。");

            string auth = (string)health["authMode"];
            string key = auth switch
            {
                "local" or "lan" => (string)health["token"],
                "bearer" => savedKey,
                _ => throw new InvalidDataException("TranscriptionServerの認証方式に対応していません。"),
            };
            if (string.IsNullOrEmpty(key) || key.Length > 2048 || key.Any(c => c < '!' || c > '~'))
                throw new IOException("TranscriptionServerの認証情報を取得できません。サーバーの接続設定を確認してください。");

            var catalog = await ReadStatus(client, new Uri(endpoint, "/v1/models"), key, "モデル一覧の取得",
                allowUnauthorized: false, cancellation).ConfigureAwait(false);
            if (catalog["data"] is not JsonArray rows)
                throw new InvalidDataException("TranscriptionServerのモデル一覧を読み取れません。");

            var localModels = rows.OfType<JsonObject>()
                .Where(row => row["x_meeting"] is JsonObject info &&
                    (string)info["task"] is "transcription" or "integrated" &&
                    (bool?)info["available"] == true && (bool?)info["modelInstalled"] == true)
                .Select(row => (string)row["id"])
                .Where(id => id != null && id.Split('/') is [ { Length: > 0 }, { Length: > 0 } ])
                .ToArray();
            if (requestedModel != null)
            {
                if (!localModels.Contains(requestedModel, StringComparer.Ordinal))
                    throw new IOException("指定したモデルはTranscriptionServerで利用できません。モデルID（engine/model）と導入状態を確認してください。");
                return (key, requestedModel);
            }

            string model = localModels.FirstOrDefault(id => !id.EndsWith(".en", StringComparison.OrdinalIgnoreCase))
                ?? localModels.FirstOrDefault();
            if (model == null)
                throw new IOException("TranscriptionServerに利用可能なローカル文字起こしモデルがありません。");
            return (key, model);
        }

        static async Task<JsonObject> ReadStatus(HttpClient client, Uri url, string key, string operation,
            bool allowUnauthorized, CancellationToken cancellation)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (key != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (allowUnauthorized && response.StatusCode == HttpStatusCode.Unauthorized) return null;
                if (!response.IsSuccessStatusCode) throw RequestFailure(response.StatusCode, operation);
                byte[] body = await TranscriptionApi.ReadResponse(response.Content, timeout.Token).ConfigureAwait(false);
                return JsonNode.Parse(body) as JsonObject ??
                    throw new InvalidDataException("TranscriptionServerから有効な状態情報を取得できません。");
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new IOException("TranscriptionServerの" + operation + "がタイムアウトしました。");
            }
        }

        public static IOException RequestFailure(HttpStatusCode status, string operation)
        {
            string detail = status switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "接続キーまたは接続許可を確認してください。",
                HttpStatusCode.ServiceUnavailable => "サーバーの準備状況と受付状態を確認してください。",
                _ => "サーバーの稼働状況とモデルを確認してください。",
            };
            return new IOException($"TranscriptionServerの{operation}に失敗しました（HTTP {(int)status}）。{detail}");
        }
    }
}
