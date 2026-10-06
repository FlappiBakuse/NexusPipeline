import {test} from 'node:test';import assert from 'node:assert/strict';
import {proof} from '../main/supervisor';
test('Host and client authentication share a vector and separate direction',()=>{
  const args=[Buffer.alloc(32,42),'a'.repeat(64),'b'.repeat(32),'c'.repeat(64),'d'.repeat(64),'e'.repeat(32),'f'.repeat(64)] as const;
  assert.equal(proof(args[0],'host',...args.slice(1) as [string,string,string,string,string,string]),'53544f697a62965c4195b0164f9b1c979b25045c2cbdf4aaab975d6893ff533b');
  assert.equal(proof(args[0],'client',...args.slice(1) as [string,string,string,string,string,string]),'6f874428fb969e711b8a38bc2e8dd263109f9771f87bc3ec46d60381067d7bb7');
});
