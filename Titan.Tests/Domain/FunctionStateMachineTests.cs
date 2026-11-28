using Titan.Domain.Aggregates.Function;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// The moves of a function (titan-ops features/ovc-function/design.md#function-lifecycle): a deploy and a delete wait on
// their command and end by its report; a give-up rests where the function was; a function the cluster never heard of
// is deleted at once; observations move only Active and NotFound at rest.
public class FunctionStateMachineTests
{
    private static Function AFunction() => TestFunctions.AFunction();

    // ── create ──

    [Fact]
    public void Create_IsInactive_OnItsCluster_WithNothingOnIt()
    {
        Guid tenantId = Guid.NewGuid();
        Function function = Function.Create(tenantId, "fn-dev", "hello");

        Assert.Equal(FunctionStatus.Inactive, function.Status);
        Assert.Equal(tenantId, function.TenantId);
        Assert.Equal("fn-dev", function.ClusterId);
        Assert.Null(function.ExternalId);
        Assert.Null(function.RunningVersionId);
        Assert.Null(function.DeployingVersionId);
        Assert.Null(function.Operation.PendingId);
        Assert.Empty(function.Versions);
    }

    // ── deploy ──

    [Fact]
    public void Deploy_WaitsOnItsCommand_ForTheVersion()
    {
        Function function = AFunction();
        FunctionVersion version = function.WithVersion();

        Guid operationId = function.Deploy(version.Id);

        Assert.Equal(FunctionStatus.Deploying, function.Status);
        Assert.Equal(version.Id, function.DeployingVersionId);
        Assert.Equal(operationId, function.Operation.PendingId);
        Assert.NotNull(function.Operation.AskedAt);
        Assert.Null(function.RunningVersionId);
    }

    [Fact]
    public void Deploy_OfAVersionItDoesNotHave_Is404()
    {
        Function function = AFunction();
        function.WithVersion();

        var e = Assert.Throws<NotFoundException>(() => function.Deploy(Guid.NewGuid()));

        Assert.Equal("FUNCTION_VERSION_NOT_FOUND", e.Code);
        Assert.Equal(FunctionStatus.Inactive, function.Status);
    }

    [Fact]
    public void Deploy_OfADiscardedVersion_Is404()
    {
        Function function = AFunction();
        FunctionVersion first = function.WithVersion();
        for (int i = 0; i < Function.KeptVersions; i++) function.WithVersion();
        Assert.True(first.IsDiscarded);

        var e = Assert.Throws<NotFoundException>(() => function.Deploy(first.Id));

        Assert.Equal("FUNCTION_VERSION_NOT_FOUND", e.Code);
    }

    [Fact]
    public void Deploy_WhileAnotherWaits_OrOfTheRunningVersion_IsInvalidStatus()
    {
        Function function = AFunction();
        FunctionVersion v1 = function.WithVersion();
        FunctionVersion v2 = function.WithVersion();
        function.Deploy(v1.Id);

        Assert.Equal("INVALID_STATUS", Assert.Throws<BusinessException>(() => function.Deploy(v2.Id)).Code);
        Assert.Equal("INVALID_STATUS", Assert.Throws<BusinessException>(() => function.Deploy(v1.Id)).Code);

        function.DeploySucceeded(function.Operation.PendingId!.Value, "fn-a");
        Assert.Equal("INVALID_STATUS", Assert.Throws<BusinessException>(() => function.Deploy(v1.Id)).Code);
    }

    [Fact]
    public void Deploy_OfADeletingOrDeletedFunction_IsInvalidStatus()
    {
        Function deleting = AFunction();
        FunctionVersion version = deleting.Running();
        deleting.Delete();
        Function deleted = AFunction();
        FunctionVersion other = deleted.WithVersion();
        deleted.Delete();

        Assert.Equal("INVALID_STATUS", Assert.Throws<BusinessException>(() => deleting.Deploy(version.Id)).Code);
        Assert.Equal("INVALID_STATUS", Assert.Throws<BusinessException>(() => deleted.Deploy(other.Id)).Code);
    }

    [Fact]
    public void IsDeployWaitingOrDone_IsTheWaitingVersion_OrTheOneThatRunsWhileActive()
    {
        Function function = AFunction();
        FunctionVersion v1 = function.WithVersion();
        FunctionVersion v2 = function.WithVersion();
        Assert.False(function.IsDeployWaitingOrDone(v1.Id));

        Guid operationId = function.Deploy(v1.Id);
        Assert.True(function.IsDeployWaitingOrDone(v1.Id));
        Assert.False(function.IsDeployWaitingOrDone(v2.Id));

        function.DeploySucceeded(operationId, "fn-a");
        Assert.True(function.IsDeployWaitingOrDone(v1.Id));
        Assert.False(function.IsDeployWaitingOrDone(v2.Id));

        // The Service is gone: deploying the running version again puts it back.
        function.MarkNotFound();
        Assert.False(function.IsDeployWaitingOrDone(v1.Id));
    }

    [Fact]
    public void DeploySucceeded_RunsTheVersion_NamesTheService_AndClearsAnOlderFailure()
    {
        Function function = AFunction();
        FunctionVersion v1 = function.WithVersion();
        function.DeployFailed(function.Deploy(v1.Id), null, "revision not ready", "detail");

        bool ended = function.DeploySucceeded(function.Deploy(v1.Id), "fn-abc");

        Assert.True(ended);
        Assert.Equal(FunctionStatus.Active, function.Status);
        Assert.Equal(v1.Id, function.RunningVersionId);
        Assert.Null(function.DeployingVersionId);
        Assert.Equal("fn-abc", function.ExternalId);
        Assert.Null(function.Operation.PendingId);
        Assert.Null(function.Operation.FailureReason);
    }

    [Fact]
    public void DeploySucceeded_KeepsTheFirstServiceName()
    {
        Function function = AFunction();
        function.Running();
        string name = function.ExternalId!;
        FunctionVersion v2 = function.WithVersion();

        function.DeploySucceeded(function.Deploy(v2.Id), "fn-other");

        Assert.Equal(name, function.ExternalId);
        Assert.Equal(v2.Id, function.RunningVersionId);
    }

    [Fact]
    public void DeploySucceeded_WithoutAServiceName_OnAFunctionWithoutOne_Is422_AndChangesNothing()
    {
        Function function = AFunction();
        FunctionVersion version = function.WithVersion();
        Guid operationId = function.Deploy(version.Id);

        var e = Assert.Throws<BusinessException>(() => function.DeploySucceeded(operationId, null));

        Assert.Equal("INVALID_STATUS", e.Code);
        Assert.Equal(FunctionStatus.Deploying, function.Status);
        Assert.Equal(operationId, function.Operation.PendingId);
        Assert.Null(function.RunningVersionId);
    }

    [Fact]
    public void DeployFailed_OfTheFirstDeploy_IsInactiveAgain_WithTheReason()
    {
        Function function = AFunction();
        FunctionVersion version = function.WithVersion();

        bool ended = function.DeployFailed(function.Deploy(version.Id), null, "revision not ready", "raw");

        Assert.True(ended);
        Assert.Equal(FunctionStatus.Inactive, function.Status);
        Assert.Null(function.RunningVersionId);
        Assert.Null(function.DeployingVersionId);
        Assert.Equal("revision not ready", function.Operation.FailureReason);
        Assert.Equal("raw", function.Operation.FailureDetail);
        Assert.Null(function.Operation.PendingId);
    }

    [Fact]
    public void DeployFailed_OfALaterDeploy_IsActive_WithTheOldVersionStillRunning()
    {
        Function function = AFunction();
        FunctionVersion v1 = function.Running();
        FunctionVersion v2 = function.WithVersion();

        function.DeployFailed(function.Deploy(v2.Id), null, "revision not ready", null);

        Assert.Equal(FunctionStatus.Active, function.Status);
        Assert.Equal(v1.Id, function.RunningVersionId);
        Assert.Null(function.DeployingVersionId);
    }

    [Fact]
    public void DeployFailed_FromNotFound_RestsNotFound()
    {
        Function function = AFunction();
        FunctionVersion v1 = function.Running();
        function.MarkNotFound();

        function.DeployFailed(function.Deploy(v1.Id), null, "pull failed", null);

        Assert.Equal(FunctionStatus.NotFound, function.Status);
    }

    [Fact]
    public void DeployFailed_ThatBringsTheServiceName_KeepsIt_SoADeleteReachesTheCluster()
    {
        Function function = AFunction();
        FunctionVersion version = function.WithVersion();
        function.DeployFailed(function.Deploy(version.Id), "fn-made", "revision not ready", null);

        Assert.Equal(FunctionStatus.Inactive, function.Status);
        Assert.Equal("fn-made", function.ExternalId);
        Assert.NotNull(function.Delete());
        Assert.Equal(FunctionStatus.Deleting, function.Status);
    }

    // ── delete ──

    [Fact]
    public void Delete_OfAFunctionTheClusterNeverHeardOf_IsDoneAtOnce_WithoutATicket()
    {
        Function function = AFunction();
        function.WithVersion();

        Assert.Null(function.Delete());

        Assert.Equal(FunctionStatus.Deleted, function.Status);
        Assert.Null(function.Operation.PendingId);
    }

    [Fact]
    public void Delete_OfARunningFunction_WaitsOnItsCommand_WithATicketNamingTheService()
    {
        Function function = AFunction();
        function.Running();

        var ticket = function.Delete();

        Assert.NotNull(ticket);
        Assert.Equal(function.ExternalId, ticket.ProviderId);
        Assert.Equal(function.Operation.PendingId, ticket.OperationId);
        Assert.Equal(FunctionStatus.Deleting, function.Status);
    }

    [Fact]
    public void Delete_WhileADeployWaits_OrRepeated_IsInvalidStatus()
    {
        Function deploying = AFunction();
        deploying.Deploy(deploying.WithVersion().Id);
        Function deleting = AFunction();
        deleting.Running();
        deleting.Delete();
        Function deleted = AFunction();
        deleted.Delete();

        Assert.Equal("INVALID_STATUS", Assert.Throws<BusinessException>(() => deploying.Delete()).Code);
        Assert.Equal("INVALID_STATUS", Assert.Throws<BusinessException>(() => deleting.Delete()).Code);
        Assert.Equal("INVALID_STATUS", Assert.Throws<BusinessException>(() => deleted.Delete()).Code);
    }

    [Fact]
    public void DeleteSucceeded_IsDeleted_AndEveryVersionsCodeIsTheCleanupsToRemove()
    {
        Function function = AFunction();
        function.Running();
        function.WithVersion();

        bool ended = function.DeleteSucceeded(function.Delete()!.OperationId);

        Assert.True(ended);
        Assert.Equal(FunctionStatus.Deleted, function.Status);
        Assert.True(function.IsDeleted);
        Assert.False(function.OccupiesQuota);
        Assert.Equal(2, function.VersionsToClean.Count());
    }

    [Fact]
    public void DeleteFailed_RestsWhereItWas_WithTheReason()
    {
        Function function = AFunction();
        FunctionVersion v1 = function.Running();

        bool ended = function.DeleteFailed(function.Delete()!.OperationId, "api refused", "raw");

        Assert.True(ended);
        Assert.Equal(FunctionStatus.Active, function.Status);
        Assert.Equal(v1.Id, function.RunningVersionId);
        Assert.Equal("api refused", function.Operation.FailureReason);
        Assert.True(function.OccupiesQuota);
    }

    [Fact]
    public void ADeployReport_WhileADeleteWaits_IsIgnored()
    {
        Function function = AFunction();
        function.Running();
        function.Delete();

        Assert.False(function.DeploySucceeded(function.Operation.PendingId!.Value, "fn-x"));
        Assert.Equal(FunctionStatus.Deleting, function.Status);
    }

    [Fact]
    public void OccupiesQuota_UntilTheDeleteIsDone()
    {
        Function function = AFunction();
        Assert.True(function.OccupiesQuota);
        function.Running();
        function.Delete();
        Assert.True(function.OccupiesQuota);
    }

    // ── observation ──

    [Fact]
    public void Observation_MovesActiveAndNotFound_AtRest()
    {
        Function function = AFunction();
        function.Running();

        function.MarkNotFound();
        Assert.Equal(FunctionStatus.NotFound, function.Status);
        Assert.False(function.IsSeenOnTheCluster);

        function.MarkObserved();
        Assert.Equal(FunctionStatus.Active, function.Status);
        Assert.True(function.IsSeenOnTheCluster);
    }

    [Fact]
    public void Observation_OfAnInactiveFunction_ChangesNothing()
    {
        Function function = AFunction();
        FunctionVersion version = function.WithVersion();
        function.DeployFailed(function.Deploy(version.Id), "fn-made", "revision not ready", null);

        function.MarkObserved();
        Assert.Equal(FunctionStatus.Inactive, function.Status);
        function.MarkNotFound();
        Assert.Equal(FunctionStatus.Inactive, function.Status);
    }

    [Fact]
    public void Observation_WhileADeployWaits_OnlyRecordsWhatWasSeen_AndAGiveUpRestsOnIt()
    {
        Function function = AFunction();
        function.Running();
        Guid operationId = function.Deploy(function.WithVersion().Id);

        function.MarkNotFound();
        Assert.Equal(FunctionStatus.Deploying, function.Status);
        Assert.Equal(FunctionStatus.NotFound, function.Operation.LastSeenStatus);
        Assert.Equal(operationId, function.Operation.PendingId);

        function.DeployFailed(operationId, null, "pull failed", null);
        Assert.Equal(FunctionStatus.NotFound, function.Status);
    }

    [Fact]
    public void Observation_WhileTheFirstDeployWaits_LeavesNeverSeenAsNeverSeen()
    {
        Function function = AFunction();
        function.Deploy(function.WithVersion().Id);

        function.MarkNotFound();

        Assert.Null(function.Operation.LastSeenStatus);
    }

    [Fact]
    public void Deleted_AbsorbsEveryObservation()
    {
        Function function = AFunction();
        function.Running();
        function.DeleteSucceeded(function.Delete()!.OperationId);

        function.MarkObserved();
        Assert.Equal(FunctionStatus.Deleted, function.Status);
        function.MarkNotFound();
        Assert.Equal(FunctionStatus.Deleted, function.Status);
        Assert.False(function.IsReconcilable);
    }

    [Fact]
    public void IsReconcilable_OnceTheServiceIsNamed_UntilDeleted()
    {
        Function function = AFunction();
        Assert.False(function.IsReconcilable);
        function.Running();
        Assert.True(function.IsReconcilable);
    }
}
