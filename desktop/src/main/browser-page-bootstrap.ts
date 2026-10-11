export interface PageBootstrap {origin:string;path:string;script:string}
interface Page {identity:unknown;url:string;run(code:string):Promise<unknown>}
interface Timing {now():number;pause():Promise<void>}

export class BrowserPageBootstrap {
  private generation=0;
  private started=false;
  constructor(private readonly rule:PageBootstrap|null|undefined,private readonly page:()=>Page|null,
    private readonly timing:Timing={now:()=>performance.now(),pause:()=>new Promise(resolve=>setTimeout(resolve,250))}) {}
  navigation(){this.stop();this.started=false;}
  inPageNavigation(url:string){if(!this.rule||!this.matches(url))this.stop();}
  stop(){this.generation++;}
  async loaded():Promise<void> {
    if(this.started||!this.rule)return;
    this.started=true;
    const rule=this.rule,first=this.page(),generation=this.generation;
    if(!first||!this.matches(first.url))return;
    const end=this.timing.now()+30000,wallEnd=Date.now()+30000;
    while(generation===this.generation&&this.timing.now()<end) {
      const page=this.page();
      if(!page||page.identity!==first.identity||!this.matches(page.url))return;
      try {
        let timeout:ReturnType<typeof setTimeout>|undefined;
        const remaining=Math.max(1,end-this.timing.now());
        const result=await Promise.race([
          page.run(`(()=>{if(Date.now()>${wallEnd}||location.origin!==${JSON.stringify(rule.origin)}||location.pathname!==${JSON.stringify(rule.path)})return 'stale';return (${rule.script})();})()`),
          new Promise(resolve=>{timeout=setTimeout(()=>resolve('expired'),remaining);})
        ]).finally(()=>clearTimeout(timeout));
        if(result!=='waiting')return;
      }catch{return;}
      if(generation!==this.generation)return;
      await this.timing.pause();
    }
  }
  private matches(value:string):boolean {
    try{const url=new URL(value);return url.protocol==='https:'&&!url.username&&!url.password&&url.origin===this.rule!.origin&&url.pathname===this.rule!.path;}catch{return false;}
  }
}
