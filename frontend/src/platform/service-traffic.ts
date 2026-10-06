let paused=false;
const listeners=new Set<(paused: boolean)=>void>();
export function serviceTrafficPaused(): boolean {return paused;}
export function setServiceTrafficPaused(value: boolean): void {
  if(paused===value)return;paused=value;
  for(const listener of listeners)listener(value);
}
export function onServiceTrafficChanged(listener: (paused: boolean)=>void): ()=>void {listeners.add(listener);return ()=>listeners.delete(listener);}
export function requireServiceTraffic(): void {if(paused)throw new DOMException('Service connection is awaiting recovery','AbortError');}
