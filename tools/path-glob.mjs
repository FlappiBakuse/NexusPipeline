/** Compile repository-relative * and ** patterns without changing path semantics. */
function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&");
}

export function globToRegExp(glob) {
  const segments = String(glob).split("/");
  let source = "^";
  for (let index = 0; index < segments.length; index++) {
    const segment = segments[index];
    const isLast = index === segments.length - 1;
    if (segment === "**") {
      if (isLast) {
        source += ".*";
        break;
      }
      source += "(?:[^/]+/)*";
      continue;
    }
    source += escapeRegExp(segment).replaceAll("\\*", "[^/]*");
    if (!isLast) source += "/";
  }
  return new RegExp(`${source}$`, "u");
}
