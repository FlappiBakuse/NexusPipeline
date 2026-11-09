const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

export async function checkAmbientMotion(page) {
  const script = expression => page.evaluate(expression);
  const media = value => page.emulateMedia({ reducedMotion: value });
  const draws = () => script('globalThis.ambientMotion.draws');
  const progressed = async (label, baseline) => {
    const deadline = Date.now() + 3000;
    while (Date.now() < deadline) {
      const value = await draws();
      if (value > baseline + 4) return value;
      await delay(30);
    }
    throw new Error(label + ' did not draw within 3 seconds');
  };
  const stable = async label => {
    const first = await draws(); await delay(110);
    if (await draws() !== first) throw new Error(label + ' scheduled another ambient frame');
    return first;
  };
  await media('reduce');
  await script(`(async()=>{
    const draw = CanvasRenderingContext2D.prototype.clearRect;
    const state = { draws: 0, draw };
    globalThis.ambientMotion = state;
    CanvasRenderingContext2D.prototype.clearRect = function(...args) {
      if (this.canvas.classList.contains('ambient-layer')) state.draws++;
      return draw.apply(this, args);
    };
    const { createApp, h, ref, nextTick } = await import('vue');
    const { default: AmbientLayer } = await import('/apps/web/src/app/AmbientLayer.vue');
    const visible = ref(true), holder = document.createElement('div');
    holder.style.cssText = 'width:320px;height:200px'; document.body.append(holder);
    const app = createApp({ setup: () => () => h(AmbientLayer, { visible: visible.value }) });
    app.mount(holder); Object.assign(state, { visible, holder, app, nextTick });
  })()`);
  try {
    await progressed('Initial reduced motion', -4);
    const reduced = await stable('Reduced motion');
    const painted = await script(`(() => {
      const canvas = globalThis.ambientMotion.holder.querySelector('canvas');
      const pixels = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
      return pixels.some((value, index) => index % 4 === 3 && value > 0);
    })()`);
    if (!painted) throw new Error('Reduced motion omitted its static background');
    await media('no-preference');
    const resumed = await progressed('Normal motion', reduced);
    await media('reduce'); await delay(180);
    const stopped = await stable('Normal to reduced motion');
    await script('(async()=>{globalThis.ambientMotion.visible.value=false; await globalThis.ambientMotion.nextTick(); return true})()');
    await delay(80); const hidden = await stable('Hidden ambient');
    await media('no-preference'); await delay(180);
    if (await draws() !== hidden) throw new Error('Hidden preference change scheduled an ambient frame');
    await script('(async()=>{globalThis.ambientMotion.visible.value=true; await globalThis.ambientMotion.nextTick(); return true})()');
    const shown = await progressed('Visible ambient', hidden);
    const unmounted = await script(`(()=>{ const state=globalThis.ambientMotion;
      state.app.unmount(); state.unmounted=true; state.holder.remove();
      return { draws: state.draws }; })()`);
    await media('reduce'); await media('no-preference'); await delay(180);
    if (await draws() !== unmounted.draws)
      throw new Error('Unmount kept ambient frames');
    return { reduced, resumed, stopped, hidden, shown, unmounted };
  } finally {
    await script(`(()=>{ const state=globalThis.ambientMotion;
      if (state) { if (!state.unmounted) state.app?.unmount(); state.holder?.remove();
        CanvasRenderingContext2D.prototype.clearRect=state.draw;
        delete globalThis.ambientMotion; } })()`);
    await media('no-preference');
  }
}
export async function checkImageOwnership(page) {
  const script = expression => page.evaluate(expression);
  return await script(`(async () => {
    const { createApp, h, ref, nextTick } = await import('vue');
    const { default: MediaImage } = await import('/apps/web/src/components/MediaImage.vue');
    const canvas=document.createElement('canvas'); canvas.width=640; canvas.height=480;
    const context=canvas.getContext('2d'); context.fillStyle='rgb(24,112,208)'; context.fillRect(0,0,640,480);
    const blob=await new Promise(resolve=>canvas.toBlob(resolve,'image/png'));
    const url=URL.createObjectURL(blob), holder=document.createElement('div');
    holder.style.cssText='position:fixed;left:20px;top:20px;width:320px;height:200px;z-index:10000';
    document.body.append(holder);
    const visible=ref(true), source=ref('first'), preview=ref(false); let acquired=0,released=0,aborted=0,lateResolve;
    const pool={acquire:async(path,signal)=>{
      acquired++; signal.addEventListener('abort',()=>aborted++,{once:true});
      let done=false; const lease={url,blob:path==='bad'?new Blob([new Uint8Array([0])]):blob,
        width:640,height:480,release(){if(!done){done=true;released++;}}};
      return path==='late' ? await new Promise(resolve=>{lateResolve=()=>resolve(lease);}) : lease;
    }};
    const app=createApp({setup:()=>()=>h(MediaImage,{pool,path:source.value,visible:visible.value,preview:preview.value})});
    const wait=async(condition,label)=>{const end=Date.now()+3000;while(!condition()){
      if(Date.now()>end)throw new Error(label);await new Promise(resolve=>setTimeout(resolve,10));}};
    app.mount(holder); let unmounted=false;
    try {
      await wait(()=>holder.querySelector('canvas')?.width===640,'Original image lost its source resolution');
      const image=()=>holder.querySelector('canvas');
      const cleared=()=>!image() || (image().width===0 && image().height===0);
      const pixel=image().getContext('2d').getImageData(10,10,1,1).data;
      if(pixel[0]!==24||pixel[1]!==112||pixel[2]!==208||pixel[3]!==255)throw new Error('Original pixels were not drawn');
      for(let cycle=0;cycle<100;cycle++){
        visible.value=false; await nextTick();
        await wait(()=>released===acquired&&cleared(),'Hidden image kept its bitmap or lease');
        visible.value=true; await nextTick();
        await wait(()=>image()?.width===640&&image()?.height===480,'Reopened image did not paint its original lease');
      }
      visible.value=false; await nextTick();
      await wait(()=>released===acquired,'Last visible lease remained held');
      preview.value=true; visible.value=true; await nextTick();
      const physicalWidth=Math.min(640,holder.querySelector('figure').clientWidth*devicePixelRatio);
      await wait(()=>image()?.width>=physicalWidth&&image()?.width<=640&&image()?.width<=physicalWidth*2&&
        image()?.height===Math.round(480*image()?.width/640), 'Preview exceeded its bounded display resolution or lost its aspect ratio');
      const previewWidth=image()?.width;
      visible.value=false; await nextTick();
      source.value='bad'; visible.value=true; await nextTick();
      await wait(()=>released===acquired&&holder.querySelector('figcaption')?.textContent.includes('decoded'),
        'Decode failure retained its lease or bitmap');
      if(image()?.width||image()?.height)throw new Error('Failed decode retained pixels');
      visible.value=false; await nextTick();
      source.value='late'; visible.value=true; await nextTick();
      await wait(()=>typeof lateResolve==='function','Late image request did not start');
      visible.value=false; await nextTick(); lateResolve();
      await wait(()=>released===acquired,'Late hidden image did not release its returned lease');
      if(image()?.width||image()?.height)throw new Error('Late hidden response repopulated the image');
      app.unmount(); unmounted=true;
      if(holder.childElementCount)throw new Error('Unmount retained the image DOM');
      return {cycles:100,acquired,released,aborted,originalResolution:[640,480],previewWidth,
        decodeFailureReleased:true,lateResponseReleased:true};
    } finally {if(!unmounted)app.unmount();holder.remove();URL.revokeObjectURL(url);}
  })()`);
}
