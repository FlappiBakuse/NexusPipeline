import assert from "node:assert/strict";
import test from "node:test";
import { quoteWindowsArg } from "../support/windows-command.mjs";

test("Windows command quoting preserves ordinary executable arguments", () => {
  assert.equal(quoteWindowsArg("dotnet"), "dotnet");
  assert.equal(quoteWindowsArg("--nologo"), "--nologo");
});

test("Windows command quoting handles spaces, Chinese, and parentheses", () => {
  assert.equal(
    quoteWindowsArg("D:\\Projects\\验收包 (2026)\\NexusPipeline.exe"),
    '"D:\\Projects\\验收包 (2026)\\NexusPipeline.exe"');
});

test("Windows command quoting escapes quoted and cmd metacharacter arguments", () => {
  assert.equal(
    quoteWindowsArg('say "quoted" & %PATH%'),
    '"say ^"quoted^" ^& %%PATH%%"');
});
