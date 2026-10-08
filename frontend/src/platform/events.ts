import { apiStream, isAbortError, type ApiError } from "./api";
import { t } from "./i18n";
import { isCurrent, state, trackController, releaseController } from "./page-state";
import {onServiceTrafficChanged,serviceTrafficPaused} from './service-traffic';

export interface ParsedSseEvent {
  event: string;
  data: string;
  id?: string;
}

export interface RealtimeEventEnvelope {
  schemaVersion?: number;
  sequence?: number;
  timestamp?: string;
  data?: Record<string, unknown>;
}

export interface RealtimeSseEvent extends RealtimeEventEnvelope {
  type: string;
}

export interface EventStreamOptions {
  path?: string;
  page?: string;
  token?: number;
  signal?: AbortSignal | null;
  onEvent?: (event: RealtimeSseEvent) => void | Promise<void>;
  onReady?: (event: RealtimeSseEvent) => void | Promise<void>;
  onDisconnected?: () => void;
  onMissed?: (event: RealtimeSseEvent) => void | Promise<void>;
  onFatal?: (reason: unknown) => void;
}

export interface EventStreamHandle {
  close: () => void;
  done: Promise<void>;
}

/** 可在任意 chunk 边界调用的 SSE parser；保留 event/data/id 语义，不依赖浏览器 EventSource。 */
export class SseParser {
  private buffer = "";

  private eventName = "";

  private dataLines: string[] = [];

  private lastEventId: string | undefined;

  private readonly emit: (event: ParsedSseEvent) => void;

  public constructor(emit: (event: ParsedSseEvent) => void) {
    this.emit = emit;
  }

  public push(chunk: string): void {
    this.buffer += chunk;
    let newline = this.buffer.indexOf("\n");
    while (newline >= 0) {
      let line = this.buffer.slice(0, newline);
      this.buffer = this.buffer.slice(newline + 1);
      if (line.endsWith("\r")) line = line.slice(0, -1);
      this.processLine(line);
      newline = this.buffer.indexOf("\n");
    }
  }

  public finish(): void {
    if (this.buffer.length > 0) {
      const line = this.buffer.endsWith("\r") ? this.buffer.slice(0, -1) : this.buffer;
      this.buffer = "";
      this.processLine(line);
    }
    this.dispatch();
  }

  private processLine(line: string): void {
    if (line.length === 0) {
      this.dispatch();
      return;
    }
    if (line.startsWith(":")) return;
    const separator = line.indexOf(":");
    const field = separator >= 0 ? line.slice(0, separator) : line;
    let value = separator >= 0 ? line.slice(separator + 1) : "";
    if (value.startsWith(" ")) value = value.slice(1);
    switch (field) {
      case "event":
        this.eventName = value;
        break;
      case "data":
        this.dataLines.push(value);
        break;
      case "id":
        if (!value.includes("\u0000")) this.lastEventId = value;
        break;
      default:
        break;
    }
  }

  private dispatch(): void {
    if (this.dataLines.length === 0) {
      this.eventName = "";
      return;
    }
    this.emit({
      event: this.eventName || "message",
      data: this.dataLines.join("\n"),
      ...(this.lastEventId === undefined ? {} : { id: this.lastEventId }),
    });
    this.eventName = "";
    this.dataLines = [];
  }
}

export function parseSseChunks(chunks: Iterable<string>): ParsedSseEvent[] {
  const events: ParsedSseEvent[] = [];
  const parser = new SseParser(event => events.push(event));
  for (const chunk of chunks) parser.push(chunk);
  parser.finish();
  return events;
}

const RETRY_DELAYS = [500, 1000, 2000, 5000, 10000];
type Subscriber = {options: EventStreamOptions; usable: ()=>boolean; close: ()=>void};
const subscribers=new Set<Subscriber>();
let owners=0;
let managementListener: ((event: RealtimeSseEvent)=>void)|null=null;
let transport: SharedTransport|null=null;
let stopTrafficObserver: (()=>void)|null=null;

class SharedTransport {
  private readonly controller=new AbortController();
  private retryIndex=0;
  private retryTimer: ReturnType<typeof setTimeout>|null=null;
  ready: RealtimeSseEvent|null=null;
  readonly done: Promise<void>;
  constructor() {this.done=Promise.resolve().then(()=>this.connect());}
  stop(): void {this.controller.abort();if(this.retryTimer)clearTimeout(this.retryTimer);this.retryTimer=null;this.ready=null;}
  private async dispatch(event: RealtimeSseEvent): Promise<void> {
    if(this.controller.signal.aborted)return;
    if(event.type==='management.page-refresh'){managementListener?.(event);return;}
    if(event.type==='stream.ready'){this.ready=event;this.retryIndex=0;}
    await Promise.all([...subscribers].map(async subscriber=>{
      if(!subscriber.usable())return;
      const options=subscriber.options;
      if(event.type==='stream.ready')await options.onReady?.(event);
      else if(event.type==='stream.missed')await options.onMissed?.(event);
      else await options.onEvent?.(event);
    }));
  }
  private async consume(response: Response): Promise<void> {
    if(!response.body)throw new Error(t('api.error.stream_body_unavailable'));
    const reader=response.body.getReader(),decoder=new TextDecoder();
    let chain=Promise.resolve();
    const parser=new SseParser(event=>{
      let payload: RealtimeEventEnvelope;
      try {payload=JSON.parse(event.data) as RealtimeEventEnvelope;}catch{return;}
      if(event.event==='management.page-refresh')void this.dispatch({...payload,type:event.event});
      else chain=chain.then(()=>this.dispatch({...payload,type:event.event}));
    });
    const abort=()=>{void reader.cancel().catch(()=>{});};
    this.controller.signal.addEventListener('abort',abort,{once:true});
    try {
      while(!this.controller.signal.aborted){const next=await reader.read();if(next.done)break;parser.push(decoder.decode(next.value,{stream:true}));}
      parser.push(decoder.decode());parser.finish();await chain;
    } finally {this.controller.signal.removeEventListener('abort',abort);reader.releaseLock();}
  }
  private async connect(): Promise<void> {
    if(this.controller.signal.aborted)return;
    try {
      const response=await apiStream('/api/events',this.controller.signal);
      if(this.controller.signal.aborted){await response.body?.cancel();return;}
      await this.consume(response);
    } catch(reason) {
      if(this.controller.signal.aborted||isAbortError(reason))return;
      const status=(reason as Partial<ApiError>)?.status;
      if(typeof status==='number'&&status>=400&&status<500){
        for(const subscriber of [...subscribers]){if(subscriber.usable())subscriber.options.onFatal?.(reason);subscriber.close();}
        this.stop();if(transport===this)transport=null;return;
      }
    }
    if(this.controller.signal.aborted)return;
    this.ready=null;
    for(const subscriber of subscribers)if(subscriber.usable())subscriber.options.onDisconnected?.();
    const delay=RETRY_DELAYS[Math.min(this.retryIndex++,RETRY_DELAYS.length-1)];
    this.retryTimer=setTimeout(()=>{this.retryTimer=null;void this.connect();},delay);
  }
}
function ensureTransport(): SharedTransport|null {
  if(!stopTrafficObserver)stopTrafficObserver=onServiceTrafficChanged(paused=>{
    if(paused){transport?.stop();transport=null;}
    else if(owners||subscribers.size)ensureTransport();
  });
  return serviceTrafficPaused()?null:transport??(transport=new SharedTransport());
}
function stopIfUnused(): void {
  if(!owners&&!subscribers.size){transport?.stop();transport=null;stopTrafficObserver?.();stopTrafficObserver=null;}
}

export function startApplicationEventStream(onManagement: (event: RealtimeSseEvent)=>void): ()=>void {
  owners++;managementListener=onManagement;ensureTransport();
  let active=true;
  return ()=>{if(!active)return;active=false;owners--;if(!owners)managementListener=null;stopIfUnused();};
}

export function openEventStream(options: EventStreamOptions): EventStreamHandle {
  if(options.path&&options.path!=='/api/events')throw new Error('unsupported_event_stream');
  const page=options.page||state.page,token=options.token??state.routeToken;
  const controller=trackController(new AbortController());
  let closed=false;
  const close=()=>{
    if(closed)return;
    closed=true;subscribers.delete(subscriber);controller.abort();releaseController(controller);
    options.signal?.removeEventListener('abort',close);stopIfUnused();
  };
  const subscriber: Subscriber={options,close,usable:()=>!closed&&!controller.signal.aborted&&isCurrent(page,token)};
  subscribers.add(subscriber);
  controller.signal.addEventListener('abort',close,{once:true});
  options.signal?.addEventListener('abort',close,{once:true});
  if(options.signal?.aborted)close();
  const shared=closed?null:ensureTransport();
  if(shared?.ready)void Promise.resolve().then(()=>{if(subscriber.usable())return options.onReady?.(shared.ready!);});
  return {close,done:shared?.done??Promise.resolve()};
}
