"""Select the declared methods and their actual native assembly owners before building."""
from binary_inputs import PROJECTS
from test_expectations import suite_classes

PYTHON_GROUPS = {'tooling', 'host-boundary'}
DATA_CLASSES = {'DateFilterTests', 'RecordStoreTests', 'RecordSummaryTests', 'RecordTimingTests'}
DATA_ASSEMBLY = 'NexusPipeline.StorageDataTests'

def assembly_for(identity):
    owner = identity.split('.')
    if len(owner) < 3 or not identity.startswith('NexusPipeline.'):
        raise ValueError('Native selection has no assembly owner')
    assembly = '.'.join(owner[:2])
    return DATA_ASSEMBLY if assembly == 'NexusPipeline.StorageTests' and owner[2] in DATA_CLASSES else assembly

def select_tests(root, group, methods=None):
    selected = list(methods or [])
    if group in PYTHON_GROUPS:
        if len(selected) > 1:
            raise ValueError('Python accepts one exact method ID')
        projects = []
        if group == 'host-boundary':
            projects = ['NexusPipeline.Host', 'OwnedWriter', 'TargetGame']
        return {'methods': selected, 'projects': projects, 'assemblies': [], 'python': True}
    classes = suite_classes(root, group)
    if any(not any(identity.startswith(owner + '.') for owner in classes) for identity in selected):
        raise ValueError('Selected native method is outside this suite')
    chosen = selected or classes
    assemblies = sorted({assembly_for(identity) for identity in chosen})
    if assemblies == [DATA_ASSEMBLY]:
        projects = [DATA_ASSEMBLY]
    else:
        projects = list(PROJECTS[group])
        if DATA_ASSEMBLY in assemblies:
            projects.append(DATA_ASSEMBLY)
    return {'methods': chosen, 'projects': sorted(set(projects)), 'assemblies': assemblies, 'python': False}
