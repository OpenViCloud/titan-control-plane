namespace Titan.Application.Commands.Catalog.VmTemplate.Contracts;

// Admin trigger (and the one-shot `sync-templates` CLI through TemplateSyncRunner): ask a sync of every template in every cluster that lacks it. No parameters.
public record SyncTemplatesCommand();
