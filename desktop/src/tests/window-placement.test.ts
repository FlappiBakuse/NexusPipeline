import {test} from 'node:test';import assert from 'node:assert/strict';
import {chooseWindowPlacement,DisplaySnapshot,validSaved} from '../main/window-placement';
function placement(width: number,height: number,scale=1,workHeight=height/scale-48){const display: DisplaySnapshot={id:1,bounds:{x:0,y:0,width:width/scale,height:height/scale},workArea:{x:0,y:0,width:width/scale,height:workHeight},scaleFactor:scale,rotation:0};return chooseWindowPlacement({displays:[display],primaryDisplayId:1,saved:null,launchIntent:'show',titleBarHeightDip:40,frameInsetsDip:{width:0,height:0}});}
test('physical resolution tiers descend against DIP work area',()=>{
  for(const [width,height,scale,expected] of [[1920,1080,1,1280],[2560,1440,1,1920],[3840,2160,1,2560],[3840,2160,1.5,1920],[3840,2160,2,1280]]){const result=placement(width,height,scale);assert.equal(result.kind,'show');if(result.kind==='show')assert.equal(result.businessSizeDip.width,expected);}
  assert.equal(placement(1920,1080,1.5,688).kind,'needs-low-resolution-choice');
});
test('background never requires a display or opens a low resolution prompt',()=>{assert.equal(chooseWindowPlacement({displays:[],primaryDisplayId:1,saved:null,launchIntent:'background',titleBarHeightDip:40,frameInsetsDip:{width:0,height:0}}).kind,'background');});
test('invalid remembered geometry cannot enter a native window',()=>{assert.equal(validSaved({schemaVersion:1,x:NaN,y:0,businessWidth:1280,businessHeight:720,displayId:1,scaleFactor:1,maximized:false}),false);});
