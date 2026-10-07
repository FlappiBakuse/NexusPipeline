import fs from "node:fs";
import path from "node:path";
import {ClientPreferences} from "../shared/contracts";
export function atomicJson(file: string,value: unknown): void {
  fs.mkdirSync(path.dirname(file),{recursive:true});
  if(fs.existsSync(file)&&fs.lstatSync(file).isSymbolicLink()) throw new Error("Linked client state");
  const temporary=file+"."+process.pid+".tmp";
  fs.writeFileSync(temporary,JSON.stringify(value),{flag:"wx"});
  fs.renameSync(temporary,file);
}
export class Preferences {
  private value: ClientPreferences={locale:"zh-CN",theme:"system"};
  constructor(private readonly file: string) {
    if(!fs.existsSync(file)) return;
    try {
      if(fs.lstatSync(file).isSymbolicLink()||fs.statSync(file).size>4096) return;
      const record=JSON.parse(fs.readFileSync(file,"utf8"));
      if(record.schemaVersion===1) this.value=this.validate(record.preferences);
    } catch { }
  }
  get(): ClientPreferences {return Object.freeze({...this.value});}
  set(patch: Partial<ClientPreferences>): void {
    if(!patch||typeof patch!=="object"||Object.keys(patch).some(key=>key!=="locale"&&key!=="theme")) throw new Error("Invalid client preference fields");
    const next=this.validate({...this.value,...patch});
    atomicJson(this.file,{schemaVersion:1,preferences:next});this.value=next;
  }
  private validate(value: ClientPreferences): ClientPreferences {
    if(!value||!['zh-CN','en-US'].includes(value.locale)||!['light','dark','system'].includes(value.theme)||Object.keys(value).length!==2) throw new Error("Invalid client preferences");
    return {locale:value.locale,theme:value.theme};
  }
}
