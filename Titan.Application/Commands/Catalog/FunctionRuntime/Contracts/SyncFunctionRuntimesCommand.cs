namespace Titan.Application.Commands.Catalog.FunctionRuntime.Contracts;

// Admin trigger: ask a sync of every runtime in every function cluster that lacks it and has no sync waiting. No parameters.
public record SyncFunctionRuntimesCommand();
