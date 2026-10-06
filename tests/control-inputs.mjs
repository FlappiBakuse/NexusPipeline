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

export function workingTreeSha(root) {
  const git=(args,input)=>execFileSync('git',['-C',root,...args],{input,encoding:'utf8',maxBuffer:16*1024*1024});
  const modes=new Map(git(['ls-files','--stage','-z']).split('\0').filter(Boolean).map(entry=>{
    const match=/^(\d+) [0-9a-f]{40} 0\t(.+)$/.exec(entry);
    if(!match)throw new Error('Unmerged source cannot form build inputs');
    return [match[2],match[1]];
  }));
  const files=[...new Set([...modes.keys(),...git(['ls-files','--others','--exclude-standard','-z']).split('\0').filter(Boolean)])];
  const attributes=git(['check-attr','-z','--stdin','text'],files.join('\0')+'\0').split('\0');
  const text=new Map();for(let index=0;index<attributes.length-1;index+=3)text.set(attributes[index],attributes[index+2]);
  const object=(kind,bytes)=>createHash('sha1').update(Buffer.from(`${kind} ${bytes.length}\0`)).update(bytes).digest();
  const tree=new Map();
  for(const relative of files) {
    const file=path.resolve(root,relative);
    if(!fs.existsSync(file))continue;
    if(path.relative(root,file).startsWith('..')||fs.lstatSync(file).isSymbolicLink()||!fs.statSync(file).isFile())throw new Error('Unsafe build source');
    let bytes=fs.readFileSync(file);
    const attribute=text.get(relative);
    if(attribute==='set'||attribute==='auto'&&!bytes.includes(0))bytes=Buffer.from(bytes.toString('latin1').replaceAll('\r\n','\n'),'latin1');
    const segments=relative.split('/');let directory=tree;
    for(const name of segments.slice(0,-1)){if(!directory.has(name))directory.set(name,new Map());directory=directory.get(name);}
    directory.set(segments.at(-1),{mode:modes.get(relative)??'100644',digest:object('blob',bytes)});
  }
  const encode=directory=>object('tree',Buffer.concat([...directory].sort(([left,a],[right,b])=>
    Buffer.from(left+(a instanceof Map?'/':'')).compare(Buffer.from(right+(b instanceof Map?'/':'')))).map(([name,value])=>
    Buffer.concat([Buffer.from(`${value instanceof Map?'40000':value.mode} ${name}\0`),value instanceof Map?encode(value):value.digest]))));
  return encode(tree).toString('hex');
}
