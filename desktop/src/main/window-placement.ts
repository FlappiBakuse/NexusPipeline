export interface Rectangle { x: number; y: number; width: number; height: number; }
export interface DisplaySnapshot { id: number; bounds: Rectangle; workArea: Rectangle; scaleFactor: number; rotation: number; }
export interface SavedPlacement { schemaVersion: 1; x: number; y: number; businessWidth: number; businessHeight: number; displayId: number; scaleFactor: number; maximized: boolean; }
export interface PlacementInput { displays: DisplaySnapshot[]; primaryDisplayId: number; saved: SavedPlacement | null; launchIntent: "show" | "background"; titleBarHeightDip: 40; frameInsetsDip: { width: number; height: number }; }
export type PlacementResult = { kind: "background"; reason: string } | {
  kind: "show"; displayId: number; businessSizeDip: {width: number; height: number}; contentSizeDip: {width: number; height: number}; outerBoundsDip: Rectangle;
  maximized: boolean; source: "remembered" | "resolution-tier";
} | { kind: "needs-low-resolution-choice"; displayId: number; requiredBusinessSizeDip: {width: 1280; height: 720}; availableWorkAreaDip: Rectangle };
const finite = (...values: number[]) => values.every(Number.isFinite);
export function validSaved(value: unknown): value is SavedPlacement {
  if (!value || typeof value !== "object") return false;
  const v = value as SavedPlacement;
  return v.schemaVersion === 1 && typeof v.maximized === "boolean" && finite(v.x,v.y,v.businessWidth,v.businessHeight,v.displayId,v.scaleFactor)
    && v.businessWidth >= 1280 && v.businessHeight >= 720 && v.businessWidth <= 32768 && v.businessHeight <= 32768 && v.scaleFactor > 0 && v.scaleFactor <= 8;
}
function intersection(a: Rectangle,b: Rectangle) { return Math.max(0,Math.min(a.x+a.width,b.x+b.width)-Math.max(a.x,b.x))*Math.max(0,Math.min(a.y+a.height,b.y+b.height)-Math.max(a.y,b.y)); }
export function chooseWindowPlacement(input: PlacementInput): PlacementResult {
  if (input.launchIntent === "background") return {kind:"background",reason:"launch-intent"};
  const displays=input.displays.filter(d=>finite(d.id,d.scaleFactor,d.workArea.x,d.workArea.y,d.workArea.width,d.workArea.height,d.bounds.width,d.bounds.height)
    && d.scaleFactor>0 && d.workArea.width>0 && d.workArea.height>0 && d.bounds.width>0 && d.bounds.height>0);
  if (!displays.length || !finite(input.frameInsetsDip.width,input.frameInsetsDip.height) || input.frameInsetsDip.width<0 || input.frameInsetsDip.height<0) throw new Error("Invalid display geometry");
  const saved=validSaved(input.saved)?input.saved:null;
  let display=displays.find(d=>d.id===saved?.displayId);
  if (!display && saved) {
    const old={x:saved.x,y:saved.y,width:saved.businessWidth,height:saved.businessHeight+40};
    display=displays.toSorted((a,b)=>intersection(old,b.workArea)-intersection(old,a.workArea))[0];
    if(intersection(old,display.workArea)===0) display=undefined;
  }
  display ??= displays.find(d=>d.id===input.primaryDisplayId)??displays[0];
  const area=display.workArea, inset=input.frameInsetsDip;
  const available={width:area.width-inset.width,height:area.height-inset.height-40};
  if(available.width<1280||available.height<720) return {kind:"needs-low-resolution-choice",displayId:display.id,requiredBusinessSizeDip:{width:1280,height:720},availableWorkAreaDip:{...area}};
  let width=0,height=0,source: "remembered"|"resolution-tier"="resolution-tier";
  if(saved) {
    const factor=Math.min(1,available.width/saved.businessWidth,available.height/saved.businessHeight);
    const w=Math.floor(saved.businessWidth*factor),h=Math.floor(saved.businessHeight*factor);
    if(w>=1280&&h>=720) {width=w;height=h;source="remembered";}
  }
  if(!width) {
    const edge=Math.round(Math.min(display.bounds.width,display.bounds.height)*display.scaleFactor);
    const tiers=edge>=2160?[[2560,1440],[1920,1080],[1280,720]]:edge>=1440?[[1920,1080],[1280,720]]:[[1280,720]];
    [width,height]=tiers.find(([w,h])=>w<=available.width&&h<=available.height)!;
  }
  const outerWidth=width+inset.width,outerHeight=height+40+inset.height;
  const x=source==="remembered"?saved!.x:area.x+(area.width-outerWidth)/2;
  const y=source==="remembered"?saved!.y:area.y+(area.height-outerHeight)/2;
  return {kind:"show",displayId:display.id,businessSizeDip:{width,height},contentSizeDip:{width,height:height+40},outerBoundsDip:{x:Math.round(Math.max(area.x,Math.min(x,area.x+area.width-outerWidth))),y:Math.round(Math.max(area.y,Math.min(y,area.y+area.height-outerHeight))),width:outerWidth,height:outerHeight},maximized:saved?.maximized??false,source};
}
