using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Shoreline.Api.Features;
using Shoreline.Api.Modules.StoragePool;
using Shoreline.Api.Security;

namespace Shoreline.Tests;

/// <summary>
/// HTTP contract tests for /v1/admin/storage-pool, the port of the legacy
/// blueprint app.modules.storage_pool.api. The assertions mirror the Python
/// route handlers: the admin auth boundary on every route, field-scoped
/// validation errors, probe-before-persist, in-band probe results, the
/// transfer state machine codes, settings/scan shapes and the legacy Chinese
/// display names the admin console renders verbatim.
/// </summary>
[Collection("local-services-thumbnail")]
public sealed class StoragePoolHttpTests(LocalServicesWithoutWorker services)
{
    private const string Base = "/v1/admin/storage-pool";
    private static readonly CancellationToken Ct = CancellationToken.None;

    // Dead endpoints used below: loopback ports inside this task's dedicated
    // range with nothing listening, so the probe fails fast with ECONNREFUSED.
    private const string DeadEndpoint = "http://127.0.0.1:29259";

    // ------------------------------------------------------------------ auth

    [Fact]
    public async Task EveryRoute_RequiresAdminWithLegacyTokenErrors()
    {
        var anonymous = services.App.CreateClient();
        var (_, userClient) = await UserAsync(false);
        var badToken = services.App.CreateClient();
        badToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");

        foreach (var (method, path) in AuthRoutes())
        {
            var noToken = await SendAsync(anonymous, method, path, HasBody(method) ? "{}" : null);
            Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
            AssertError(await BodyAsync(noToken), "NeedTokenError", 1003, "Token required.");

            var denied = await SendAsync(userClient, method, path, HasBody(method) ? "{}" : null);
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            AssertError(await BodyAsync(denied), "NoPermissionError", 103, "No permission.");
        }

        var invalid = await badToken.GetAsync(Base + "/sources", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        AssertError(await BodyAsync(invalid), "BadTokenError", 1004, "Invalid token.");
    }

    // ------------------------------------------------------ source create/update

    [Fact]
    public async Task SourceCreate_EmptyAndMalformedBodies_ReturnValidateError()
    {
        var (_, admin) = await UserAsync(true);

        var empty = await admin.PostAsync(Base + "/sources",
            new StringContent(string.Empty, Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        AssertError(await BodyAsync(empty), "ValidateError", 2, "请求体不能为空");

        var absent = await admin.PostAsync(Base + "/sources", null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, absent.StatusCode);
        AssertError(await BodyAsync(absent), "ValidateError", 2, "请求体不能为空");

        var malformed = await admin.PostAsync(Base + "/sources",
            new StringContent("{oops", Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        AssertError(await BodyAsync(malformed), "ValidateError", 2, "请求体不能为空");
    }

    [Fact]
    public async Task SourceCreate_FieldValidation_ReturnsFieldScopedErrors()
    {
        var (_, admin) = await UserAsync(true);
        var name = Unique("valid");

        // Legacy marshmallow rejects the payload before any probe runs.
        var missingName = await admin.PostAsJsonAsync(Base + "/sources", new Dictionary<string, object?>());
        AssertFieldError(await BodyAsync(missingName), "name", "Missing data for required field.");

        var blankName = await admin.PostAsJsonAsync(Base + "/sources",
            With(SourcePayload(name), ("name", "")));
        AssertFieldError(await BodyAsync(blankName), "name", "不能为空");

        var addressing = await admin.PostAsJsonAsync(Base + "/sources",
            With(SourcePayload(name), ("addressing_style", "blob")));
        AssertFieldError(await BodyAsync(addressing), "addressing_style", "仅支持 path 或 virtual");

        var subPath = await admin.PostAsJsonAsync(Base + "/sources",
            With(SourcePayload(name), ("sub_path", "pool\\x")));
        AssertFieldError(await BodyAsync(subPath), "sub_path",
            "仅允许字母、数字、点、下划线、短横线和斜杠");

        var inactiveDays = await admin.PostAsJsonAsync(Base + "/sources",
            With(SourcePayload(name), ("inactive_days", 0)));
        AssertFieldError(await BodyAsync(inactiveDays), "inactive_days", "范围为 1 到 3650");

        var maxObjects = await admin.PostAsJsonAsync(Base + "/sources",
            With(SourcePayload(name), ("max_objects_per_run", 100001)));
        AssertFieldError(await BodyAsync(maxObjects), "max_objects_per_run", "范围为 1 到 100000");

        var badEndpoint = await admin.PostAsJsonAsync(Base + "/sources",
            With(SourcePayload(name), ("endpoint", "ftp://127.0.0.1:9000")));
        AssertFieldError(await BodyAsync(badEndpoint), "endpoint",
            "必须是无用户信息、查询参数和片段的 http(s) 地址");

        var queryEndpoint = await admin.PostAsJsonAsync(Base + "/sources",
            With(SourcePayload(name), ("endpoint", "http://127.0.0.1:9000/bucket?versionId=1")));
        AssertFieldError(await BodyAsync(queryEndpoint), "endpoint",
            "必须是无用户信息、查询参数和片段的 http(s) 地址");

        // Legacy rejects verify_ssl=false outright (code 4) instead of probing.
        var verifySsl = await admin.PostAsJsonAsync(Base + "/sources",
            With(SourcePayload(name), ("verify_ssl", false)));
        Assert.Equal(HttpStatusCode.BadRequest, verifySsl.StatusCode);
        AssertError(await BodyAsync(verifySsl), "StoragePoolUnreachable", 4,
            "verify_ssl=false 不受支持，已拒绝连接");

        // None of the rejected payloads may reach storage.
        Assert.Equal(0, await services.Store.Collection("storage_pool_source")
            .CountDocumentsAsync(new BsonDocument("n", name), cancellationToken: Ct));
    }

    [Fact]
    public async Task SourceCreate_UnreachableProbeIsRejectedBeforePersist()
    {
        var (_, admin) = await UserAsync(true);
        var name = Unique("dead");

        var response = await admin.PostAsJsonAsync(Base + "/sources",
            SourcePayload(name, endpoint: DeadEndpoint));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertError(await BodyAsync(response), "StoragePoolUnreachable", 4, "存储来源连接测试失败");
        Assert.False(await services.Store.Collection("storage_pool_source")
            .Find(new BsonDocument("n", name)).AnyAsync(Ct));
    }

    [Fact]
    public async Task SourceUpdate_UnknownOrEmptyBody_ReturnsLegacyErrors()
    {
        var (_, admin) = await UserAsync(true);

        var unknown = await admin.PutAsJsonAsync($"{Base}/sources/{ObjectId.GenerateNewId()}",
            SourcePayload(Unique("ghost")));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        AssertError(await BodyAsync(unknown), "StoragePoolSourceNotFound", 1, "存储来源不存在");

        var created = await admin.PostAsJsonAsync(Base + "/sources", SourcePayload(Unique("put")));
        var id = (await BodyAsync(created)).GetProperty("id").GetString();

        var empty = await admin.PutAsync($"{Base}/sources/{id}",
            new StringContent(string.Empty, Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        AssertError(await BodyAsync(empty), "ValidateError", 2, "请求体不能为空");

        var updated = await admin.PutAsJsonAsync($"{Base}/sources/{id}", new { remark = "updated" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("updated", (await BodyAsync(updated)).GetProperty("remark").GetString());
    }

    // ---------------------------------------------------------- source probe/test

    [Fact]
    public async Task SourceTest_ReturnsLegacyInBandProbeResult()
    {
        var (_, admin) = await UserAsync(true);

        var created = await admin.PostAsJsonAsync(Base + "/sources", SourcePayload(Unique("probe")));
        var id = (await BodyAsync(created)).GetProperty("id").GetString()!;

        var ok = await admin.PostAsync($"{Base}/sources/{id}/test", null, Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var okBody = await BodyAsync(ok);
        Assert.True(okBody.GetProperty("ok").GetBoolean());
        Assert.Equal("存储来源连接正常", okBody.GetProperty("message").GetString());

        // Legacy SourceTestAPI catches probe failures and reports HTTP 200
        // {"ok": false, ...} so the console can show the reason verbatim.
        var deadId = await InsertSourceAsync(endpoint: DeadEndpoint);
        var failed = await admin.PostAsync($"{Base}/sources/{deadId}/test", null, Ct);
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        var failedBody = await BodyAsync(failed);
        Assert.False(failedBody.GetProperty("ok").GetBoolean());
        Assert.Equal("存储来源连接测试失败", failedBody.GetProperty("message").GetString());

        var missing = await admin.PostAsync($"{Base}/sources/{ObjectId.GenerateNewId()}/test", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        AssertError(await BodyAsync(missing), "StoragePoolSourceNotFound", 1, "存储来源不存在");
    }

    [Fact]
    public async Task SourceDelete_ReturnsLegacyMessageAndProtectsReferencedRows()
    {
        var (_, admin) = await UserAsync(true);

        var created = await admin.PostAsJsonAsync(Base + "/sources", SourcePayload(Unique("del")));
        var id = (await BodyAsync(created)).GetProperty("id").GetString()!;

        var deleted = await admin.DeleteAsync($"{Base}/sources/{id}", Ct);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var deletedBody = await BodyAsync(deleted);
        Assert.Equal("已删除", deletedBody.GetProperty("message").GetString());
        Assert.True(deletedBody.GetProperty("deleted").GetBoolean());

        var again = await admin.DeleteAsync($"{Base}/sources/{id}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        AssertError(await BodyAsync(again), "StoragePoolSourceNotFound", 1, "存储来源不存在");

        var used = await InsertSourceAsync();
        await InsertProjectAsync(csid: used);
        var inUse = await admin.DeleteAsync($"{Base}/sources/{used}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
        AssertError(await BodyAsync(inUse), "StoragePoolSourceInUse", 2, "存储来源仍有项目在用");

        var busy = await InsertSourceAsync();
        var holder = await InsertProjectAsync();
        await InsertTransferAsync(holder, busy, StoragePoolConstants.Pending);
        var busyResponse = await admin.DeleteAsync($"{Base}/sources/{busy}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, busyResponse.StatusCode);
        AssertError(await BodyAsync(busyResponse), "StoragePoolSourceBusy", 6, "存储来源有进行中的迁移");
    }

    [Fact]
    public async Task SourcesList_MasksSecretsAndAppliesPaginationHeaders()
    {
        var (_, admin) = await UserAsync(true);
        var normalized = await InsertSourceAsync(prefix: "pool/no-trailing-slash");
        await InsertSourceAsync();
        var total = await services.Store.Collection("storage_pool_source")
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

        var page = await admin.GetAsync($"{Base}/sources?limit=1&page=1", Ct);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("1", Header(page, "X-Pagination-Page"));
        Assert.Equal("1", Header(page, "X-Pagination-Limit"));
        Assert.Equal(total.ToString(), Header(page, "X-Pagination-Count"));
        Assert.Equal(total.ToString(), Header(page, "X-Pagination-Page-Count"));
        var rows = (await BodyAsync(page)).EnumerateArray().ToList();
        Assert.Single(rows);

        var all = await admin.GetAsync($"{Base}/sources?limit=500", Ct);
        Assert.Equal("100", Header(all, "X-Pagination-Limit"));
        var allBody = await BodyAsync(all);
        var mine = allBody.EnumerateArray().First(row =>
            row.GetProperty("id").GetString() == normalized.ToString());
        Assert.Equal("pool/no-trailing-slash/", mine.GetProperty("sub_path").GetString());
        Assert.Equal("loca...only", mine.GetProperty("access_key_id").GetString());
        Assert.False(mine.TryGetProperty("secret_access_key", out _));
        Assert.False(mine.TryGetProperty("sk", out _));
        Assert.Equal(JsonValueKind.String, mine.GetProperty("create_time").ValueKind);

        var overflow = await admin.GetAsync($"{Base}/sources?page=2147483647&limit=2", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, overflow.StatusCode);
        AssertError(await BodyAsync(overflow), "ValidateError", 2, "Page is too large");
    }

    // ------------------------------------------------------------ transfers

    [Fact]
    public async Task TransfersList_FiltersByProjectAndReturnsLegacyView()
    {
        var (_, admin) = await UserAsync(true);
        var sourceId = await InsertSourceAsync();
        var inboundProject = await InsertProjectAsync();
        await InsertTransferAsync(inboundProject, sourceId, StoragePoolConstants.Pending);
        var outboundProject = await InsertProjectAsync();
        await InsertTransferAsync(outboundProject, sourceId, StoragePoolConstants.Cancelled,
            direction: StoragePoolConstants.DirectionOut);

        var inbound = await admin.GetAsync($"{Base}/transfers?project_id={inboundProject}", Ct);
        Assert.Equal(HttpStatusCode.OK, inbound.StatusCode);
        Assert.Equal("1", Header(inbound, "X-Pagination-Count"));
        var inboundRow = Assert.Single((await BodyAsync(inbound)).EnumerateArray());
        Assert.Equal(0, inboundRow.GetProperty("status").GetInt32());
        // Legacy TransferStatus.details: the console renders these verbatim.
        Assert.Equal("待执行", inboundRow.GetProperty("status_name").GetString());
        Assert.Equal(0, inboundRow.GetProperty("direction").GetInt32());
        Assert.Equal("迁入存储池", inboundRow.GetProperty("direction_name").GetString());
        Assert.Equal(sourceId.ToString(), inboundRow.GetProperty("source").GetProperty("id").GetString());
        // Legacy to_api(): an IN transfer reports its destination as the source.
        Assert.Equal(sourceId.ToString(),
            inboundRow.GetProperty("target_source").GetProperty("id").GetString());
        Assert.Equal(inboundProject.ToString(), inboundRow.GetProperty("project").GetProperty("id").GetString());
        Assert.Equal(0, inboundRow.GetProperty("progress_percent").GetInt32());
        Assert.Equal(0, inboundRow.GetProperty("breakdown").GetProperty("object_count").GetInt32());
        Assert.Equal(JsonValueKind.String, inboundRow.GetProperty("create_time").ValueKind);
        Assert.False(inboundRow.TryGetProperty("secret_access_key", out _));

        var outbound = await admin.GetAsync($"{Base}/transfers?project_id={outboundProject}", Ct);
        Assert.Equal(HttpStatusCode.OK, outbound.StatusCode);
        var outboundRow = Assert.Single((await BodyAsync(outbound)).EnumerateArray());
        Assert.Equal("已取消", outboundRow.GetProperty("status_name").GetString());
        Assert.Equal("回迁到默认存储", outboundRow.GetProperty("direction_name").GetString());
        // Legacy to_api(): only MOVE carries a stored target; OUT has none.
        Assert.Equal(JsonValueKind.Null, outboundRow.GetProperty("target_source").ValueKind);

        // Legacy ListAPI: an unparseable project_id yields an empty page, not 500.
        var invalid = await admin.GetAsync($"{Base}/transfers?project_id=not-an-id", Ct);
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Equal(0, (await BodyAsync(invalid)).GetArrayLength());
        Assert.Equal("0", Header(invalid, "X-Pagination-Count"));
    }

    [Fact]
    public async Task TransferCancel_DrivesPendingToCancelledAndRejectsOtherStates()
    {
        var (_, admin) = await UserAsync(true);
        var sourceId = await InsertSourceAsync();
        var projectId = await InsertProjectAsync();
        var transferId = await InsertTransferAsync(projectId, sourceId, StoragePoolConstants.Pending);

        var cancelled = await admin.PostAsync($"{Base}/transfers/{transferId}/cancel", null, Ct);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var cancelledBody = await BodyAsync(cancelled);
        Assert.Equal(StoragePoolConstants.Cancelled, cancelledBody.GetProperty("status").GetInt32());
        Assert.Equal("已取消", cancelledBody.GetProperty("status_name").GetString());
        Assert.Equal("管理员取消排队任务", cancelledBody.GetProperty("last_error").GetString());

        var again = await admin.PostAsync($"{Base}/transfers/{transferId}/cancel", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        AssertError(await BodyAsync(again), "StoragePoolTransferInProgress", 7, "迁移任务不在排队状态");

        var absent = await admin.PostAsync($"{Base}/transfers/{ObjectId.GenerateNewId()}/cancel", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, absent.StatusCode);
        AssertError(await BodyAsync(absent), "StoragePoolTransferInProgress", 7, "迁移任务不在排队状态");

        var badId = await admin.PostAsync($"{Base}/transfers/not-an-id/cancel", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, badId.StatusCode);
        AssertError(await BodyAsync(badId), "StoragePoolTransferNotFound", 1, "迁移任务不存在");
    }

    [Fact]
    public async Task TransferDeleteAndRetry_EnforceLegacyStateRules()
    {
        var (_, admin) = await UserAsync(true);
        var sourceId = await InsertSourceAsync();

        // Legacy DeleteAPI: a missing row reports success.
        var missing = await admin.DeleteAsync($"{Base}/transfers/{ObjectId.GenerateNewId()}", Ct);
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
        Assert.True((await BodyAsync(missing)).GetProperty("deleted").GetBoolean());

        var pendingProject = await InsertProjectAsync();
        var pending = await InsertTransferAsync(pendingProject, sourceId, StoragePoolConstants.Pending);
        var pendingDelete = await admin.DeleteAsync($"{Base}/transfers/{pending}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, pendingDelete.StatusCode);
        AssertError(await BodyAsync(pendingDelete), "StoragePoolTransferInProgress", 7, "只能删除已取消或已完成的任务");

        var warnedProject = await InsertProjectAsync();
        var warned = await InsertTransferAsync(warnedProject, sourceId, StoragePoolConstants.Done,
            lastError: "部分对象复制失败");
        var warnedDelete = await admin.DeleteAsync($"{Base}/transfers/{warned}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, warnedDelete.StatusCode);
        AssertError(await BodyAsync(warnedDelete), "StoragePoolTransferInProgress", 7, "任务仍有残留告警，不能删除");

        var cancellableProject = await InsertProjectAsync();
        var cancellable = await InsertTransferAsync(cancellableProject, sourceId, StoragePoolConstants.Cancelled);
        var deleted = await admin.DeleteAsync($"{Base}/transfers/{cancellable}", Ct);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.True((await BodyAsync(deleted)).GetProperty("deleted").GetBoolean());
        Assert.False(await services.Store.Collection("storage_transfer")
            .Find(new BsonDocument("_id", cancellable)).AnyAsync(Ct));

        // Retry: unknown row -> 404 code 1, non-retryable -> 409 code 7.
        var retryMissing = await admin.PostAsync($"{Base}/transfers/{ObjectId.GenerateNewId()}/retry", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, retryMissing.StatusCode);
        AssertError(await BodyAsync(retryMissing), "StoragePoolTransferNotFound", 1, "迁移任务不存在");

        var doneProject = await InsertProjectAsync();
        var done = await InsertTransferAsync(doneProject, sourceId, StoragePoolConstants.Done);
        var retryDone = await admin.PostAsync($"{Base}/transfers/{done}/retry", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, retryDone.StatusCode);
        AssertError(await BodyAsync(retryDone), "StoragePoolTransferInProgress", 7, "任务不可重试");

        // A locked project blocks retry before any rebuild/dispatch happens.
        var lockedProject = await InsertProjectAsync(locked: true);
        var failedLocked = await InsertTransferAsync(lockedProject, sourceId, StoragePoolConstants.Failed);
        var retryLocked = await admin.PostAsync($"{Base}/transfers/{failedLocked}/retry", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, retryLocked.StatusCode);
        AssertError(await BodyAsync(retryLocked), "StoragePoolNotAllowed", 5, "项目存储位置已锁定");

        // Success returns the transfer view; the actual rerun is dispatched
        // asynchronously (TransferService.Dispatch), so only the envelope is
        // asserted here.
        var freshProject = await InsertProjectAsync();
        var failed = await InsertTransferAsync(freshProject, sourceId, StoragePoolConstants.Failed);
        var retried = await admin.PostAsync($"{Base}/transfers/{failed}/retry", null, Ct);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        var retriedBody = await BodyAsync(retried);
        Assert.Equal(failed.ToString(), retriedBody.GetProperty("id").GetString());
        Assert.True(retriedBody.TryGetProperty("status_name", out _));
    }

    // -------------------------------------------------------------- projects

    [Fact]
    public async Task ProjectMigrateRestoreMove_ValidateBeforeSideEffects()
    {
        var (_, admin) = await UserAsync(true);
        var sourceId = await InsertSourceAsync();

        var missingProject = await admin.PostAsJsonAsync($"{Base}/projects/{ObjectId.GenerateNewId()}/migrate",
            new { source_id = sourceId.ToString() });
        Assert.Equal(HttpStatusCode.NotFound, missingProject.StatusCode);
        AssertError(await BodyAsync(missingProject), "ProjectNotExistError", 1001, "Project does not exist.");

        var projectId = await InsertProjectAsync();

        var emptyBody = await admin.PostAsync($"{Base}/projects/{projectId}/migrate",
            new StringContent(string.Empty, Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, emptyBody.StatusCode);
        AssertError(await BodyAsync(emptyBody), "ValidateError", 2, "请求体不能为空");

        var missingSource = await admin.PostAsJsonAsync($"{Base}/projects/{projectId}/migrate",
            new Dictionary<string, object?>());
        AssertFieldError(await BodyAsync(missingSource), "source_id", "Missing data for required field.");

        var badSource = await admin.PostAsJsonAsync($"{Base}/projects/{projectId}/migrate",
            new { source_id = "not-an-id" });
        AssertFieldError(await BodyAsync(badSource), "source_id", "格式不正确");

        var notPooled = await admin.PostAsync($"{Base}/projects/{projectId}/restore", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, notPooled.StatusCode);
        AssertError(await BodyAsync(notPooled), "StoragePoolNotAllowed", 5, "项目不在存储来源中");

        var defaultProject = await InsertProjectAsync();
        var moveNoTarget = await admin.PostAsJsonAsync($"{Base}/projects/{defaultProject}/move",
            new Dictionary<string, object?>());
        Assert.Equal(HttpStatusCode.Conflict, moveNoTarget.StatusCode);
        AssertError(await BodyAsync(moveNoTarget), "StoragePoolNotAllowed", 5, "项目已位于默认存储");

        var moveBadTarget = await admin.PostAsJsonAsync($"{Base}/projects/{projectId}/move",
            new { target_source_id = "not-an-id" });
        AssertFieldError(await BodyAsync(moveBadTarget), "target_source_id", "目标存储来源不存在");

        // Locked projects are rejected by migrate/restore/move alike.
        var lockedProject = await InsertProjectAsync(locked: true);
        var lockedMigrate = await admin.PostAsJsonAsync($"{Base}/projects/{lockedProject}/migrate",
            new { source_id = sourceId.ToString() });
        Assert.Equal(HttpStatusCode.Conflict, lockedMigrate.StatusCode);
        AssertError(await BodyAsync(lockedMigrate), "StoragePoolNotAllowed", 5, "项目存储位置已锁定");

        // Migrate succeeds and returns the legacy transfer view: direction
        // names in Chinese and an IN transfer whose target_source equals its
        // source (legacy models.StorageTransfer.to_api).
        var migrate = await admin.PostAsJsonAsync($"{Base}/projects/{projectId}/migrate",
            new { source_id = sourceId.ToString() });
        Assert.Equal(HttpStatusCode.OK, migrate.StatusCode);
        var migrateBody = await BodyAsync(migrate);
        Assert.Equal(StoragePoolConstants.DirectionIn, migrateBody.GetProperty("direction").GetInt32());
        Assert.Equal("迁入存储池", migrateBody.GetProperty("direction_name").GetString());
        Assert.Equal(StoragePoolConstants.Pending, migrateBody.GetProperty("status").GetInt32());
        Assert.Equal("待执行", migrateBody.GetProperty("status_name").GetString());
        Assert.Equal(sourceId.ToString(), migrateBody.GetProperty("source").GetProperty("id").GetString());
        Assert.Equal(sourceId.ToString(), migrateBody.GetProperty("target_source").GetProperty("id").GetString());
        Assert.Equal(projectId.ToString(), migrateBody.GetProperty("project").GetProperty("id").GetString());

        // A second migration of the same project is already in flight.
        var duplicate = await admin.PostAsJsonAsync($"{Base}/projects/{projectId}/migrate",
            new { source_id = sourceId.ToString() });
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(migrateBody.GetProperty("id").GetString(),
            (await BodyAsync(duplicate)).GetProperty("id").GetString());

        // A project that sits on a source cannot move back to that same source.
        var pooled = await InsertProjectAsync(csid: sourceId);
        var sameTarget = await admin.PostAsJsonAsync($"{Base}/projects/{pooled}/move",
            new { target_source_id = sourceId.ToString() });
        Assert.Equal(HttpStatusCode.Conflict, sameTarget.StatusCode);
        AssertError(await BodyAsync(sameTarget), "StoragePoolNotAllowed", 5, "目标存储来源与当前位置相同");

        var unknownLock = await admin.PostAsync($"{Base}/projects/{ObjectId.GenerateNewId()}/lock", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknownLock.StatusCode);
        AssertError(await BodyAsync(unknownLock), "ProjectNotExistError", 1001, "Project does not exist.");

        // The API dispatches newly created migrations asynchronously. Use a
        // separate project and a seeded durable active row so the lock assertion
        // is independent of whether the earlier transfer has reached a terminal
        // state.
        var activeProject = await InsertProjectAsync();
        await services.Store.Collection(StoragePoolConstants.TransferCollection).InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["p"] = activeProject,
            ["st"] = StoragePoolConstants.Pending,
        }, cancellationToken: Ct);
        var activeLock = await admin.PostAsync($"{Base}/projects/{activeProject}/lock", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, activeLock.StatusCode);
        AssertError(await BodyAsync(activeLock), "StoragePoolNotAllowed", 5, "项目有进行中的迁移，不能锁定");

        var lockProject = await InsertProjectAsync();
        var locked = await admin.PostAsync($"{Base}/projects/{lockProject}/lock", null, Ct);
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        Assert.True((await BodyAsync(locked)).GetProperty("storage_pool_locked").GetBoolean());

        var unlocked = await admin.PostAsJsonAsync($"{Base}/projects/{lockProject}/lock",
            new { locked = false });
        Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
        Assert.False((await BodyAsync(unlocked)).GetProperty("storage_pool_locked").GetBoolean());

        var badLocked = await admin.PostAsJsonAsync($"{Base}/projects/{lockProject}/lock",
            new { locked = "yes" });
        AssertFieldError(await BodyAsync(badLocked), "locked", "布尔值格式不正确");
        Assert.False((await services.Store.Collection("project")
            .Find(new BsonDocument("_id", lockProject)).FirstAsync(Ct))["splk"].AsBoolean);
    }

    // -------------------------------------------------------------- settings

    [Fact]
    public async Task Settings_ReturnLegacyShapeAndValidateRanges()
    {
        await ResetSettingsAsync();
        var (adminUser, admin) = await UserAsync(true);

        var initial = await admin.GetAsync($"{Base}/settings", Ct);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var initialBody = await BodyAsync(initial);
        Assert.False(initialBody.GetProperty("scan_enabled").GetBoolean());
        Assert.True(initialBody.GetProperty("run_enabled").GetBoolean());
        Assert.Equal(20L * 1024 * 1024 * 1024, initialBody.GetProperty("run_max_bytes").GetInt64());
        Assert.Equal(2000, initialBody.GetProperty("run_max_objects").GetInt32());
        Assert.Equal(20, initialBody.GetProperty("run_max_projects").GetInt32());
        Assert.Equal(JsonValueKind.Null, initialBody.GetProperty("updated_by").ValueKind);
        Assert.Equal("", initialBody.GetProperty("last_scan_result").GetString());
        Assert.Equal(0, initialBody.GetProperty("last_scan_queued").GetInt32());
        Assert.True(initialBody.GetProperty("next_run_at").GetString()!.Length > 19);

        var updated = await admin.PutAsJsonAsync($"{Base}/settings", new { scan_enabled = true });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var updatedBody = await BodyAsync(updated);
        Assert.True(updatedBody.GetProperty("scan_enabled").GetBoolean());
        Assert.Equal(adminUser["_id"].ToString(), updatedBody.GetProperty("updated_by").GetString());
        Assert.True(updatedBody.GetProperty("update_time").GetString()!.Length > 19);

        var audit = await services.Store.Collection("storage_pool_audit")
            .Find(new BsonDocument { ["c"] = "settings.update", ["a"] = adminUser["_id"] })
            .Sort(new BsonDocument("ct", -1)).FirstAsync(Ct);
        Assert.True(audit["v"].AsBsonDocument["se"].AsBoolean);

        var smallBytes = await admin.PutAsJsonAsync($"{Base}/settings", new { run_max_bytes = 100 });
        Assert.Equal(HttpStatusCode.BadRequest, smallBytes.StatusCode);
        AssertFieldError(await BodyAsync(smallBytes), "run_max_bytes", "不得小于 1 MiB");

        var manyObjects = await admin.PutAsJsonAsync($"{Base}/settings", new { run_max_objects = 0 });
        AssertFieldError(await BodyAsync(manyObjects), "run_max_objects", "范围为 1 到 100000");

        var manyProjects = await admin.PutAsJsonAsync($"{Base}/settings", new { run_max_projects = 5000 });
        AssertFieldError(await BodyAsync(manyProjects), "run_max_projects", "范围为 1 到 1000");

        var empty = await admin.PutAsync($"{Base}/settings",
            new StringContent(string.Empty, Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        AssertError(await BodyAsync(empty), "ValidateError", 2, "请求体不能为空");
    }

    // ---------------------------------------------------------- stats and usage

    [Fact]
    public async Task StatsAndUsage_ReflectStoredRows()
    {
        var (_, admin) = await UserAsync(true);
        var before = await CountsAsync();

        var stats = await admin.GetAsync($"{Base}/stats", Ct);
        Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
        var statsBody = await BodyAsync(stats);
        var after = await CountsAsync();

        // Background transfers dispatched by other cases may settle between the
        // two counts, so every value must sit inside its observed window.
        Assert.InRange(statsBody.GetProperty("sources").GetInt64(), before.Sources, after.Sources);
        Assert.InRange(statsBody.GetProperty("enabled_sources").GetInt64(), before.Enabled, after.Enabled);
        Assert.InRange(statsBody.GetProperty("migrated_projects").GetInt64(), before.Migrated, after.Migrated);
        Assert.InRange(statsBody.GetProperty("active_transfers").GetInt64(), before.Active, after.Active);

        var usage = await admin.GetAsync($"{Base}/usage", Ct);
        Assert.Equal(HttpStatusCode.OK, usage.StatusCode);
        var usageBody = await BodyAsync(usage);
        var total = usageBody.GetProperty("total");
        Assert.Equal(before.Projects, total.GetProperty("projects").GetInt64());
        Assert.Equal(JsonValueKind.Number, total.GetProperty("bytes").ValueKind);
        Assert.Equal(JsonValueKind.Number, total.GetProperty("cover_bytes").ValueKind);
        Assert.Equal(JsonValueKind.Number, total.GetProperty("resample_bytes").ValueKind);
        Assert.Equal(JsonValueKind.Number, total.GetProperty("output_bytes").ValueKind);
        var pending = usageBody.GetProperty("pending");
        Assert.Equal(JsonValueKind.Number, pending.GetProperty("projects").ValueKind);
        Assert.Equal(JsonValueKind.Number, pending.GetProperty("bytes").ValueKind);
        Assert.Equal(JsonValueKind.Number, usageBody.GetProperty("migrated_projects").ValueKind);
        Assert.Equal(JsonValueKind.Array, usageBody.GetProperty("by_source").ValueKind);
        Assert.Equal(JsonValueKind.Array, usageBody.GetProperty("top_projects").ValueKind);
    }

    // ------------------------------------------------------------------- scan

    [Fact]
    public async Task ScanAndLock_RejectMalformedBodyAndRecordScanState()
    {
        await ResetSettingsAsync();
        await services.Store.Collection("storage_pool_source").UpdateManyAsync(
            FilterDefinition<BsonDocument>.Empty,
            new BsonDocument("$set", new BsonDocument("e", false)), cancellationToken: Ct);
        var (_, admin) = await UserAsync(true);

        var forced = await admin.PostAsJsonAsync(Base + "/scan", new { force = true });
        Assert.Equal(HttpStatusCode.OK, forced.StatusCode);
        var forcedBody = await BodyAsync(forced);
        Assert.True(forcedBody.GetProperty("ok").GetBoolean());
        Assert.Equal("没有启用的存储来源", forcedBody.GetProperty("message").GetString());
        Assert.Equal(0, forcedBody.GetProperty("queued").GetInt32());

        var settings = await admin.GetAsync($"{Base}/settings", Ct);
        var settingsBody = await BodyAsync(settings);
        Assert.Equal("没有启用的存储来源", settingsBody.GetProperty("last_scan_result").GetString());
        Assert.True(settingsBody.GetProperty("last_scan_time").GetString()!.Length > 19);

        var badForce = await admin.PostAsJsonAsync(Base + "/scan", new { force = "yes" });
        Assert.Equal(HttpStatusCode.BadRequest, badForce.StatusCode);
        AssertFieldError(await BodyAsync(badForce), "force", "布尔值格式不正确");

        // Without force and with scan_enabled=false the scan short-circuits and
        // must not overwrite the previous scan record (legacy early return).
        var idle = await admin.PostAsync(Base + "/scan", null, Ct);
        Assert.Equal(HttpStatusCode.OK, idle.StatusCode);
        var idleBody = await BodyAsync(idle);
        Assert.True(idleBody.GetProperty("ok").GetBoolean());
        Assert.Equal("自动扫描未启用", idleBody.GetProperty("message").GetString());
        var afterIdle = await admin.GetAsync($"{Base}/settings", Ct);
        Assert.Equal("没有启用的存储来源",
            (await BodyAsync(afterIdle)).GetProperty("last_scan_result").GetString());

        // Legacy get_json(silent=False) rejects unparsable bodies with HTTP 400
        // instead of silently running the action with its defaults.
        var malformedScan = await admin.PostAsync(Base + "/scan",
            new StringContent("{oops", Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, malformedScan.StatusCode);
        AssertError(await BodyAsync(malformedScan), "ValidateError", 2, "请求体不能为空");

        using var chunkedBody = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes("{oops")));
        chunkedBody.Headers.ContentType = new("application/json");
        var chunkedScan = await admin.PostAsync(Base + "/scan", chunkedBody, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, chunkedScan.StatusCode);
        AssertError(await BodyAsync(chunkedScan), "ValidateError", 2, "请求体不能为空");

        var projectId = await InsertProjectAsync();
        var malformedLock = await admin.PostAsync($"{Base}/projects/{projectId}/lock",
            new StringContent("nope", Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, malformedLock.StatusCode);
        AssertError(await BodyAsync(malformedLock), "ValidateError", 2, "请求体不能为空");
        // The rejected body must not have locked the project.
        Assert.False((await services.Store.Collection("project")
            .Find(new BsonDocument("_id", projectId)).FirstAsync(Ct))["splk"].AsBoolean);
    }

    // --------------------------------------------------------------- helpers

    private static (string Method, string Path)[] AuthRoutes()
    {
        var source = ObjectId.GenerateNewId();
        var transfer = ObjectId.GenerateNewId();
        var project = ObjectId.GenerateNewId();
        return
        [
            ("GET", $"{Base}/sources"),
            ("POST", $"{Base}/sources"),
            ("PUT", $"{Base}/sources/{source}"),
            ("DELETE", $"{Base}/sources/{source}"),
            ("POST", $"{Base}/sources/{source}/test"),
            ("GET", $"{Base}/transfers"),
            ("POST", $"{Base}/transfers/{transfer}/retry"),
            ("POST", $"{Base}/transfers/{transfer}/cancel"),
            ("DELETE", $"{Base}/transfers/{transfer}"),
            ("POST", $"{Base}/projects/{project}/migrate"),
            ("POST", $"{Base}/projects/{project}/restore"),
            ("POST", $"{Base}/projects/{project}/move"),
            ("POST", $"{Base}/projects/{project}/lock"),
            ("GET", $"{Base}/stats"),
            ("GET", $"{Base}/usage"),
            ("GET", $"{Base}/settings"),
            ("PUT", $"{Base}/settings"),
            ("POST", $"{Base}/scan"),
        ];
    }

    private static bool HasBody(string method) => method is "POST" or "PUT";

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path, string? json)
    {
        StringContent? content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json");
        return method switch
        {
            "GET" => client.GetAsync(path, Ct),
            "POST" => client.PostAsync(path, content, Ct),
            "PUT" => client.PutAsync(path, content, Ct),
            "DELETE" => client.DeleteAsync(path, Ct),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "unsupported method"),
        };
    }

    private async Task<(BsonDocument User, HttpClient Client)> UserAsync(bool admin)
    {
        var id = ObjectId.GenerateNewId();
        var name = "sp" + id.ToString()[12..];
        var user = new BsonDocument
        {
            ["_id"] = id,
            ["e"] = id + "@example.test",
            ["n"] = name,
            ["ns"] = name,
            ["p"] = PasswordHash.Create("test-password"),
            ["admin"] = admin,
            ["c"] = DateTime.UtcNow,
        };
        await services.Store.Collection("user").InsertOneAsync(user, cancellationToken: Ct);
        var client = services.App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            services.App.Services.GetRequiredService<LegacyToken>().Issue(id.ToString(), user["p"].AsString));
        return (user, client);
    }

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N");

    private sealed class NonSeekableStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }

    private static Dictionary<string, object?> SourcePayload(string name, string? endpoint = null) => new()
    {
        ["name"] = name,
        ["endpoint"] = endpoint ?? "http://127.0.0.1:" + TestEnvironment.S3Port,
        ["bucket"] = "shoreline-test",
        ["sub_path"] = "pool/" + name + "/",
        ["access_key_id"] = "local-test-only",
        ["secret_access_key"] = "local-disposable-test-secret",
    };

    private static Dictionary<string, object?> With(
        Dictionary<string, object?> payload, (string Key, object? Value) change)
    {
        payload[change.Key] = change.Value;
        return payload;
    }

    private async Task<ObjectId> InsertSourceAsync(string? endpoint = null, bool enabled = true,
        string? prefix = null)
    {
        var id = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;
        await services.Store.Collection("storage_pool_source").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["n"] = "source-" + id,
            ["e"] = enabled,
            ["ep"] = endpoint ?? "http://127.0.0.1:" + TestEnvironment.S3Port,
            ["b"] = "shoreline-test",
            ["sp"] = prefix ?? "pool/" + id + "/",
            ["r"] = "us-east-1",
            ["as"] = "path",
            ["vs"] = true,
            ["ak"] = "local-test-only",
            ["sk"] = "local-disposable-test-secret",
            ["pd"] = "",
            ["rm"] = "",
            ["iad"] = 90,
            ["mo"] = 2000,
            ["hd"] = false,
            ["ct"] = now,
            ["ut"] = now,
        }, cancellationToken: Ct);
        return id;
    }

    private async Task<ObjectId> InsertProjectAsync(ObjectId? csid = null, bool locked = false)
    {
        var id = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;
        await services.Store.Collection("project").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["n"] = "project-" + id,
            ["st"] = 0,
            ["cip"] = false,
            ["m_e"] = now.AddDays(-2),
            ["label_revision"] = 0,
            ["splk"] = locked,
            ["fs"] = 1L,
            ["cover_bytes"] = 0L,
            ["resample_bytes"] = 0L,
            ["ob"] = 0L,
            ["csid"] = csid is null ? BsonNull.Value : csid.Value,
            ["csw"] = BsonNull.Value,
            ["ct"] = now,
        }, cancellationToken: Ct);
        return id;
    }

    private async Task<ObjectId> InsertTransferAsync(ObjectId projectId, ObjectId sourceId, int status,
        int direction = StoragePoolConstants.DirectionIn, ObjectId? targetId = null, string lastError = "")
    {
        var id = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;
        await services.Store.Collection("storage_transfer").InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["p"] = projectId,
            ["s"] = sourceId,
            ["ts"] = targetId is null ? BsonNull.Value : targetId.Value,
            ["st"] = status,
            ["dir"] = direction,
            ["set"] = now.AddDays(-2),
            ["to"] = 0,
            ["co"] = 0,
            ["processed"] = 0,
            ["skipped"] = 0,
            ["verified"] = 0,
            ["deleted"] = 0,
            ["tb"] = 0L,
            ["cb"] = 0L,
            ["bd"] = new BsonDocument { ["ob"] = 0L, ["ov"] = 0L, ["oc"] = 0 },
            ["npc"] = 0,
            ["stt"] = BsonNull.Value,
            ["ftt"] = BsonNull.Value,
            ["le"] = lastError,
            ["ct"] = now,
        }, cancellationToken: Ct);
        return id;
    }

    private Task ResetSettingsAsync() => services.Store.Collection("storage_pool_setting")
        .DeleteOneAsync(new BsonDocument("_id", "default"), Ct);

    private async Task<(long Sources, long Enabled, long Migrated, long Active, long Projects)> CountsAsync()
    {
        var sources = services.Store.Collection("storage_pool_source");
        var projects = services.Store.Collection("project");
        var transfers = services.Store.Collection("storage_transfer");
        return (
            await sources.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct),
            await sources.CountDocumentsAsync(new BsonDocument("e", true), cancellationToken: Ct),
            await projects.CountDocumentsAsync(
                new BsonDocument("csid", new BsonDocument("$ne", BsonNull.Value)), cancellationToken: Ct),
            await transfers.CountDocumentsAsync(new BsonDocument("st",
                new BsonDocument("$in", new BsonArray { 0, 1, 2, 3 })), cancellationToken: Ct),
            await projects.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct));
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Header(HttpResponseMessage response, string name) =>
        Assert.Single(response.Headers.GetValues(name));

    private static void AssertError(JsonElement body, string error, int code, string message)
    {
        Assert.Equal(error, body.GetProperty("error").GetString());
        Assert.Equal(code, body.GetProperty("code").GetInt32());
        Assert.Equal(message, body.GetProperty("message").GetString());
    }

    private static void AssertFieldError(JsonElement body, string field, string message)
    {
        Assert.Equal("ValidateError", body.GetProperty("error").GetString());
        Assert.Equal(2, body.GetProperty("code").GetInt32());
        var errors = body.GetProperty("message");
        Assert.Equal(JsonValueKind.Object, errors.ValueKind);
        Assert.True(errors.TryGetProperty(field, out var messages), $"missing field {field}");
        Assert.Equal(message, messages[0].GetString());
    }
}
