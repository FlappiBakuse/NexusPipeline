using NexusPipeline.Core.Automation;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Configuration;

internal static class DraftBindingOwner
{
    internal static AutomationBinding Resolve(AutomationCatalog catalog, Guid bindingId, DraftBindingIdentity draft)
    {
        if (bindingId == Guid.Empty || draft.UserId == Guid.Empty || draft.ScriptId == Guid.Empty ||
            catalog.Bindings.Any(binding => binding.Id == bindingId) ||
            !catalog.Scripts.Any(script => script.Id == draft.ScriptId && script.Revision == draft.ScriptRevision))
            throw new ConfigurationException("configuration_draft_binding_changed");
        return new(bindingId, 1, draft.UserId, draft.ScriptId, true, 0);
    }
}
