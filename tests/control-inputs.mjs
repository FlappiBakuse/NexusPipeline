import fs from "node:fs";
import path from "node:path";
import {execFileSync} from "node:child_process";
import {createHash} from "node:crypto";
const hash=bytes=>createHash("sha256").update(bytes).digest("hex");
export function controlManifest(root) {
  const files=[".github/workflows/ci.yml",".github/workflows/final-budget.yml"];
  const visit=(directory,recursive)=>{
    for(const entry of fs.readdirSync(path.join(root,directory),{withFileTypes:true})) {
      if(entry.isSymbolicLink()) throw new Error("Linked control input");
      const file=directory+"/"+entry.name;
      if(entry.isFile()&&/\.(mjs|py|json)$/.test(entry.name)) files.push(file);
      else if(recursive&&entry.isDirectory()&&!["node_modules","__pycache__","fixtures","bin","obj",".generated",".artifacts",".pytest_cache"].includes(entry.name)) visit(file,true);
    }
  };
  visit("tests",true);
  visit("tools",true);
  const result=[...new Set(files)].sort().map(file=>{
    const bytes=fs.readFileSync(path.join(root,file));
    if(bytes.subarray(0,3).equals(Buffer.from([239,187,191]))) throw new Error(`Control BOM: ${file}`);
    return {file,sha256:hash(new TextDecoder("utf-8",{fatal:true}).decode(bytes).replaceAll("\r\n","\n"))};
  });
  if(new Set(result.map(item=>item.file.toLowerCase())).size!==result.length) throw new Error("Case-colliding control inputs");
  return result;
}
export function sourceFingerprint(root) {
  const files=[...new Set(execFileSync("git",["-C",root,"ls-files","-z","--cached","--others","--exclude-standard"],{encoding:"utf8",maxBuffer:16*1024*1024}).split("\0").filter(Boolean))].sort();
  const entries=[];
  for(const relative of files) {
    const file=path.resolve(root,relative);
    if(!fs.existsSync(file)) continue;
    if(path.relative(root,file).startsWith("..")||fs.lstatSync(file).isSymbolicLink()||!fs.statSync(file).isFile()) throw new Error("Unsafe source entry");
    entries.push(`${relative}\0${hash(fs.readFileSync(file))}`);
  }
  if(!entries.length) throw new Error("Empty source fingerprint");
  return hash(entries.join("\n"));
}
