import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { parseMarkdown, exactPath, checkDocuments, reviewStructure } from "./markdown-docs.mjs";

function fixture(run) {
  const external = process.env.NEXUS_TEST_ARTIFACT_ROOT;
  assert.ok(external && path.isAbsolute(external), "External test root required");
  const root = fs.mkdtempSync(path.join(external, "docs-"));
  const write = (name, text) => { const file = path.join(root, name); fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, text); };
  const map = topics => write("docs/map.json", JSON.stringify({ schemaVersion: 1, topics }));
  write("docs/README.md", "# Documents\n\n[Guide](guide.md#重复-1)\n");
  write("docs/guide.md", "# Guide\n\n## 重复\n\nFirst.\n\n## 重复\n\nSecond.\n");
  map([{ id: "index", path: "README.md", authorityFor: ["navigation"] }, { id: "guide", path: "guide.md", authorityFor: ["guide"] }]);
  try { run({ root, write, map }); } finally { fs.rmSync(root, { recursive: true, force: true }); }
}

test("fenced and inline code are not headings or navigation", () => {
  const parsed = parseMarkdown('# Guide\n\n```md\n# Fake\n[x](missing.md)\n```\n`[x](missing.md)`\n');
  assert.equal(parsed.headings.length, 1); assert.deepEqual(parsed.links, []); assert.deepEqual(parsed.errors, []);
});

test("empty leaves fail but meaningful parent sections are valid", () => fixture(({root, write}) => {
  write("docs/guide.md", "# Guide\n\n## Group\n\n### Body\n\nText.\n");
  write("docs/README.md", "# Documents\n\n[Guide](guide.md)\n");
  assert.deepEqual(checkDocuments(root).failures, []);
  for (const body of ["", "<!-- draft -->", "TODO", "```text\n```", "[ref]: guide.md", '<a id="reserved"></a>']) {
    write("docs/guide.md", "# Guide\n\n## Empty\n\n" + body + "\n");
    assert.ok(checkDocuments(root).failures.some(x => x.includes("empty section")), body);
  }
}));

test("tables, images, diagrams and code are section content", () => {
  for (const body of ["| A | B |\n|---|---|\n| 1 | 2 |", "![diagram](diagram.svg)", "```mermaid\nflowchart LR\n A-->B\n```", "    code()", "```html\n<!-- example -->\n```", "```md\n## example\n```", "[Reference](guide.md)"]) {
    const doc = parseMarkdown("# Guide\n\n" + body + "\n");
    assert.equal(doc.sections.length, 1);
    assert.equal(doc.sections[0].hasBody, true, body);
  }
});

test("multiline comments cannot create headings or missing links", () => {
  const parsed = parseMarkdown("# Guide\n<!--\n## Hidden\n[x](missing.md)\n-->\nText.\n");
  assert.equal(parsed.headings.length, 1);
  assert.deepEqual(parsed.links, []);
  assert.equal(parsed.sections[0].hasBody, true);
});

test("inline code preserves literal comments and requires matching backtick runs", () => {
  for (const code of ["`<!--`", "`` ` <!-- [hidden](missing.md) ``"]) {
    const doc = parseMarkdown(`# Root\n\n${code}\n## Visible\n[link](target.md)\n`);
    assert.equal(doc.headings.length, 2);
    assert.deepEqual(doc.links.map(link => link.url), ["target.md"]);
  }
  assert.deepEqual(parseMarkdown('``unclosed [link](target.md)`').links.map(link => link.url), ["target.md"]);
});

test("heading suffix punctuation and generated anchor collisions follow rendered headings", () => {
  const doc = parseMarkdown('# Root\n## C#\nBody\n## A\nBody\n## A\nBody\n## A-1\nBody\n## Name ###\nBody\n');
  assert.equal(doc.headings[1].title, "C#");
  assert.equal(doc.headings.at(-1).title, "Name");
  assert.ok(doc.anchors.has("a-1-1"));
  assert.ok(parseMarkdown('# Root\n##\nBody\n').errors.some(error => error.includes("empty heading")));
  assert.ok(parseMarkdown('# Root\n## ###\nBody\n').errors.some(error => error.includes("empty heading")));
});

test("uppercase Markdown extensions and invalid UTF-8 cannot bypass validation", () => fixture(({root, write}) => {
  write("docs/hidden.MD", "# Hidden\n\n[bad](missing.md)\n");
  assert.ok(checkDocuments(root).failures.some(error => error.includes("hidden.MD") && error.includes("missing.md")));
  write("docs/guide.md", Buffer.from([0x23, 0x20, 0xc3, 0x28, 0x0a]));
  assert.ok(checkDocuments(root).failures.some(error => error.includes("invalid UTF-8")));
}));

test("navigation topics must reference documents, not existing directories or JSON", () => fixture(({root, map}) => {
  for (const target of [".", "map.json"]) {
    map([{id: "bad", path: target}]);
    assert.ok(checkDocuments(root).failures.some(error => error.includes("Not a Markdown document")));
  }
}));

test("structure notices are advisory and short documents stay valid", () => fixture(({root, write}) => {
  write("docs/README.md", "# Documents\n\n[Guide](guide.md)\n");
  write("docs/guide.md", "# Guide\n\n" + "Body. ".repeat(400) + "\n");
  const result = checkDocuments(root);
  assert.deepEqual(result.failures, []);
  assert.ok(result.notices.some(x => x.includes("without sections")));
  assert.deepEqual(reviewStructure("# Short\n\nOne topic.\n"), []);
  assert.ok(reviewStructure("# Root\n## Parent\n### Child\nBody.\n").some(x => x.includes("single child")));
}));
test("reference links, images, HTML and balanced destinations", () => {
  const parsed = parseMarkdown('# Guide\n[x][target] [target] ![alt](<asset (1).png>) [![badge](badge.svg)](guide.md)\n[target]: guide.md#ok\n<a href="other.md">other</a>\n');
  assert.equal(parsed.links.length, 6); assert.equal(parsed.links[2].url, "asset (1).png"); assert.deepEqual(parsed.errors, []);
});
test("malformed nesting, missing reference and unclosed fences fail", () => {
  assert.ok(parseMarkdown('[[x](a.md)](b.md)').errors.some(x => x.includes("nested")));
  assert.ok(parseMarkdown('[x][absent]').errors.some(x => x.includes("undefined")));
  assert.ok(parseMarkdown('```js\nx').errors.some(x => x.includes("Unclosed")));
});
test("duplicate Unicode headings and explicit anchors resolve", () => fixture(({ root, write }) => {
  assert.deepEqual(checkDocuments(root).failures, []);
  write("docs/README.md", '# Documents\n\n[x](guide.md#absent)\n');
  assert.ok(checkDocuments(root).failures.some(x => x.includes("Missing fragment")));
}));
test("exact case, escaped roots, absolute paths and missing paths fail", () => fixture(({ root }) => {
  assert.equal(exactPath(root, "docs", "../docs/guide.md"), path.join(root, "docs/guide.md"));
  for (const file of ["Guide.md", "../../outside.md", "missing.md", "C:/outside.md", "a\\b.md"])
    assert.throws(() => exactPath(root, "docs", file));
}));
test("map coverage, unique authority and validation paths are enforced", () => fixture(({ root, write, map }) => {
  write("docs/extra.md", "# Extra\n");
  assert.ok(checkDocuments(root).failures.some(x => x.includes("Unmapped")));
  map([{ id: "index", path: "README.md", authorityFor: ["shared"] }, { id: "guide", path: "guide.md", authorityFor: ["shared"] }]);
  assert.ok(checkDocuments(root).failures.some(x => x.includes("Duplicate authority")));
  map([{ id: "index", path: "README.md", validationPaths: ["missing.test.mjs"] }]);
  assert.ok(checkDocuments(root).failures.some(x => x.includes("missing.test.mjs")));
}));
test("title and filename rules exempt legal originals and GitHub templates", () => fixture(({ root, write }) => {
  write(".github/PULL_REQUEST_TEMPLATE.md", "## Change\n");
  write("plugins/Example/LICENSES/License.md", "Original third-party text");
  assert.deepEqual(checkDocuments(root).failures, []);
  write("docs/Bad_Name.md", "# One\n\n### Skipped\n\n# Two\n");
  const failures = checkDocuments(root).failures;
  for (const message of ["expected one H1", "skipped heading", "noncanonical filename"]) assert.ok(failures.some(x => x.includes(message)));
}));
test("partner main links check actual target bytes while historic URLs remain evidence", () => fixture(({ root, write }) => {
  const partner = fs.mkdtempSync(path.join(path.dirname(root), "partner-"));
  try {
    write("src/NexusPipeline.csproj", "<Project />");
    fs.mkdirSync(path.join(partner, "docs")); fs.writeFileSync(path.join(partner, "docs/guide.md"), "# Partner\n");
    write("docs/README.md", '# Documents\n\n[x](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/main/docs/guide.md#partner)\n[x](https://github.com/FlappiBakuse/NexusPipeline-Plugins/blob/abcdef/docs/old.md)\n');
    assert.deepEqual(checkDocuments(root, { partnerRoot: partner }).failures, []);
    fs.renameSync(path.join(partner, "docs/guide.md"), path.join(partner, "docs/other.md"));
    assert.ok(checkDocuments(root, { partnerRoot: partner }).failures.some(x => x.includes("guide.md")));
  } finally { fs.rmSync(partner, { recursive: true, force: true }); }
}));
