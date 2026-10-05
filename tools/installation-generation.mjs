import { readFileSync } from "node:fs";

const properties = readFileSync(new URL("../Directory.Build.props", import.meta.url), "utf8");
const matches = [...properties.matchAll(/<NexusInstallationGeneration>(g[0-9]+)<\/NexusInstallationGeneration>/g)];
if (matches.length !== 1) throw new Error("Invalid installation generation build field");
export const installationGeneration = matches[0][1];
export const controlServiceName = `NexusPipeline.${installationGeneration}`;
