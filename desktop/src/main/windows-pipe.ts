import koffi from "koffi";
import path from "node:path";
const kernel=koffi.load("kernel32.dll");
const create=kernel.func("void * __stdcall CreateFileW(const char16_t *name, uint32_t access, uint32_t share, void *security, uint32_t disposition, uint32_t flags, void *templateFile)");
const close=kernel.func("bool __stdcall CloseHandle(void *handle)");
const peek=kernel.func("bool __stdcall PeekNamedPipe(void *handle, void *buffer, uint32_t size, void *read, _Out_ uint32_t *available, void *remaining)");
const read=kernel.func("bool __stdcall ReadFile(void *handle, _Out_ uint8_t *buffer, uint32_t size, _Out_ uint32_t *count, void *overlapped)");
const write=kernel.func("bool __stdcall WriteFile(void *handle, const uint8_t *buffer, uint32_t size, _Out_ uint32_t *count, void *overlapped)");
const serverPid=kernel.func("bool __stdcall GetNamedPipeServerProcessId(void *handle, _Out_ uint32_t *pid)");
const openProcess=kernel.func("void * __stdcall OpenProcess(uint32_t access, bool inherit, uint32_t pid)");
const image=kernel.func("bool __stdcall QueryFullProcessImageNameW(void *handle, uint32_t flags, _Out_ uint8_t *name, _Inout_ uint32_t *size)");
const times=kernel.func("bool __stdcall GetProcessTimes(void *handle, _Out_ uint8_t *created, _Out_ uint8_t *exited, _Out_ uint8_t *kernel, _Out_ uint8_t *user)");
const delay=(ms: number)=>new Promise<void>(resolve=>setTimeout(resolve,ms));
export interface ProcessIdentity {pid: number; startFileTime: string; executablePath: string;}
export class WindowsPipe {
  private handle: unknown;
  private writing: Promise<void>=Promise.resolve();
  readonly peer: ProcessIdentity;
  constructor(name: string,expectedRoot: string) {
    if(!/^NexusPipeline\.g0170\.[0-9a-f]{24}$/.test(name)) throw new Error("Invalid supervisor pipe");
    this.handle=create("\\\\.\\pipe\\"+name,0xc0000000,0,null,3,0,null);
    if(!this.handle||koffi.address(this.handle as never)===0xffffffffffffffffn) throw new Error("desktop_pipe_unavailable");
    try {
      const pid=[0];if(!serverPid(this.handle,pid)||pid[0]<=0) throw new Error("Missing pipe peer");
      const processHandle=openProcess(0x1000,false,pid[0]);if(!processHandle) throw new Error("Unverifiable pipe peer");
      try {
        const name=Buffer.alloc(65536),size=[32768];
        const created=Buffer.alloc(8),exited=Buffer.alloc(8),cpuKernel=Buffer.alloc(8),cpuUser=Buffer.alloc(8);
        if(!image(processHandle,0,name,size)||!times(processHandle,created,exited,cpuKernel,cpuUser)) throw new Error("Unverifiable pipe peer");
        const executablePath=name.subarray(0,size[0]*2).toString("utf16le");
        if(path.resolve(executablePath).toLowerCase()!==path.join(expectedRoot,"NexusPipeline.exe").toLowerCase()) throw new Error("Unexpected pipe peer executable");
        this.peer={pid:pid[0],startFileTime:created.readBigUInt64LE().toString(),executablePath};
      } finally {close(processHandle);}
    } catch(error) {this.dispose();throw error;}
  }
  dispose() {if(this.handle){close(this.handle);this.handle=null;}}
  private async exact(size: number,deadline: number): Promise<Buffer> {
    const result=Buffer.alloc(size);let position=0;
    while(position<size) {
      if(!this.handle||Date.now()>=deadline) throw new Error("desktop_pipe_timeout");
      const available=[0];if(!peek(this.handle,null,0,null,available,null)) throw new Error("desktop_pipe_disconnected");
      if(!available[0]) {await delay(25);continue;}
      const count=[0],length=Math.min(available[0],size-position);
      if(!read(this.handle,result.subarray(position),length,count,null)||!count[0]) throw new Error("desktop_pipe_disconnected");
      position+=count[0];
    }
    return result;
  }
  async receive(timeoutMs=5000): Promise<Record<string,unknown>> {
    const deadline=Date.now()+timeoutMs,header=await this.exact(4,deadline),size=header.readUInt32LE();
    if(!size||size>65536) throw new Error("Invalid supervisor frame size");
    const bytes=await this.exact(size,deadline);
    const text=new TextDecoder("utf-8",{fatal:true}).decode(bytes);
    const value=JSON.parse(text);
    if(!value||Array.isArray(value)||typeof value!=="object") throw new Error("Invalid supervisor frame");
    if(JSON.stringify(value)!==text) throw new Error("Noncanonical supervisor frame");
    return value;
  }
  send(value: unknown): Promise<void> {
    const bytes=Buffer.from(JSON.stringify(value),"utf8");
    if(!bytes.length||bytes.length>65536) return Promise.reject(new Error("Invalid supervisor frame size"));
    const frame=Buffer.alloc(bytes.length+4);frame.writeUInt32LE(bytes.length);bytes.copy(frame,4);
    const operation=this.writing.then(()=>new Promise<void>((resolve,reject)=>{
      if(!this.handle){reject(new Error("desktop_pipe_disconnected"));return;}
      const timer=setTimeout(()=>{this.dispose();reject(new Error("desktop_pipe_timeout"));},5000);
      const count=[0];write.async(this.handle,frame,frame.length,count,null,(error: Error|null,success: boolean)=>{
        clearTimeout(timer);if(error||!success||count[0]!==frame.length) reject(new Error("desktop_pipe_disconnected"));else resolve();
      });
    }));
    this.writing=operation.catch(()=>{});return operation;
  }
}
