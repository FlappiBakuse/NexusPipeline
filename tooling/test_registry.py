"""Build native filters without duplicating discovered method or case identities."""
from test_expectations import suite_classes


def suite_filter(root, group, method, desktop_method):
    if method is not None:
        methods = [method] if isinstance(method, str) else method
        return "|".join("FullyQualifiedName=" + identity for identity in methods)
    selected = suite_classes(root, group)
    selection = "|".join("FullyQualifiedName~" + owner + "." for owner in selected)
    if group == "storage":
        excluded = suite_classes(root, "storage-config-cross-volume")
        exclusion = "&".join(["FullyQualifiedName!~" + owner + "." for owner in excluded] +
                              ["FullyQualifiedName!=" + desktop_method])
        selection = "(" + selection + ")&" + exclusion
    return selection
