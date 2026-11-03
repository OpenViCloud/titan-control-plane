namespace Titan.Application.Commands.Catalog.VmTemplate.Contracts;

// The template reconciler's observation of one cluster: whether it holds the alias. Template and cluster come from the
// route.
public record ObserveTemplateCommand(Guid TemplateId, string ClusterId, bool Exists);
