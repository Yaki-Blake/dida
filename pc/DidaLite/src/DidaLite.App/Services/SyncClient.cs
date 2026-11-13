using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DidaLite.Core;
using DidaLite.Core.Models;
using DidaLite.Data;

namespace DidaLite.App.Services;

/// <summary>PC 端同步客户端：推本地待上传的改动，同时把服务端增量拉回来，一次往返完成。</summary>
public sealed class SyncClient
{
    public const string DefaultUrl = "http://127.0.0.1:5080/sync";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly LocalStore _store;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly string _url;

    private SyncClient(LocalStore store, string url)
    {
        _store = store;
        _url = url;
    }

    /// <summary>PC 端始终走本机回环，不需要手机那套账号鉴权。</summary>
    public static SyncClient Create(LocalStore store)
    {
        var url = EnvFile.Get("DIDA_SYNC_URL");
        return new SyncClient(store, string.IsNullOrWhiteSpace(url) ? DefaultUrl : url);
    }

    public async Task<SyncStats> SyncAsync(CancellationToken ct = default)
    {
        var dirty = _store.GetDirty();
        var since = _store.GetCursor();

        using var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = JsonContent.Create(new SyncRequest { Since = since, Changes = dirty }, options: Json)
        };

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"服务端返回 HTTP {(int)response.StatusCode}");

        var payload = await response.Content.ReadFromJsonAsync<SyncResponse>(Json, ct)
                      ?? throw new InvalidOperationException("服务端返回了空内容");

        // 记下发出去时的修改时间：推送途中又改过的行不该被误清 dirty
        var sentAt = dirty.ToDictionary(t => t.Id, t => t.UpdatedAt);

        foreach (var accepted in payload.Accepted ?? [])
            _store.MarkSynced(accepted.Id, accepted.Rev, sentAt.GetValueOrDefault(accepted.Id));

        foreach (var rejected in payload.Rejected ?? [])
            if (rejected.Server is not null) _store.ApplyServer(rejected.Server);

        foreach (var task in payload.Tasks ?? []) _store.ApplyServer(task);

        _store.SetCursor(payload.MaxRev);

        return new SyncStats(payload.Accepted?.Count ?? 0, payload.Rejected?.Count ?? 0, payload.Tasks?.Count ?? 0);
    }

    private sealed class SyncResponse
    {
        public long MaxRev { get; set; }
        public List<AcceptedDto>? Accepted { get; set; }
        public List<RejectedDto>? Rejected { get; set; }
        public List<TaskItem>? Tasks { get; set; }
    }

    private sealed class AcceptedDto
    {
        public string Id { get; set; } = string.Empty;
        public long Rev { get; set; }
    }

    private sealed class RejectedDto
    {
        public string Id { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public TaskItem? Server { get; set; }
    }
}

/// <summary>一轮同步的结果，用于界面提示。</summary>
public sealed record SyncStats(int Accepted, int Rejected, int Pulled);
