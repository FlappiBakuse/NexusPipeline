"""Select the declared methods and their actual native assembly owners before building."""
from binary_inputs import PROJECTS
from test_expectations import load_expected_methods

PYTHON_GROUPS = {'tooling', 'host-boundary'}
DATA_CLASSES = {'DateFilterTests', 'RecordStoreTests', 'RecordSummaryTests', 'RecordTimingTests'}
DATA_ASSEMBLY = 'NexusPipeline.StorageDataTests'
DESKTOP_METHOD = 'NexusPipeline.StorageTests.MaintenanceBinaryTests.RestartsOwnedDesktop'
NATIVE_IDENTITY = 'test_native_identities.NativeIdentityTests.test_exact_identities'

def assembly_for(identity):
    owner = identity.rsplit('.', 2)
    if len(owner) != 3 or not identity.startswith('NexusPipeline.'):
        raise ValueError('Native method has no reviewed assembly owner')
    return DATA_ASSEMBLY if owner[0] == 'NexusPipeline.StorageTests' and owner[1] in DATA_CLASSES else owner[0]

def select_tests(root, group, methods=None):
    expected = load_expected_methods(root, group)
    selected = list(methods or [])
    if selected and not set(selected).issubset(expected):
        raise ValueError('Selected method is not declared in this group')
    chosen = {identity: expected[identity] for identity in selected} if selected else dict(expected)
    if group == 'storage' and not selected:
        chosen.pop(DESKTOP_METHOD)
    if group in PYTHON_GROUPS:
        if len(selected) > 1:
            raise ValueError('Python accepts one exact method ID')
        projects = ['NexusPipeline.CoreTests'] if NATIVE_IDENTITY in chosen else []
        if group == 'host-boundary':
            projects = ['NexusPipeline.Host', 'OwnedWriter', 'TargetGame']
        return {'methods': chosen, 'projects': projects, 'assemblies': [], 'python': True}
    assemblies = sorted({assembly_for(identity) for identity in chosen})
    if assemblies == [DATA_ASSEMBLY]:
        projects = [DATA_ASSEMBLY]
    else:
        projects = list(PROJECTS[group])
        if DATA_ASSEMBLY in assemblies:
            projects.append(DATA_ASSEMBLY)
    return {'methods': chosen, 'projects': sorted(set(projects)), 'assemblies': assemblies, 'python': False}
