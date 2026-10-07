import {test} from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import {canonical,readBuild} from '../main/host-identity';
import {runtimeLocale} from '../shared/runtime-locale';

test('legacy and profiled shared vectors use the same canonical identity',()=>{
  const fixtures=process.env.NEXUS_BUILD_IDENTITY_FIXTURES??path.resolve('../../tests/fixtures/build-identity');
  const temporary=fs.mkdtempSync(path.join(os.tmpdir(),'desktop-identity-'));
  try {
    for(const name of ['example','profile']){
      const source=JSON.parse(fs.readFileSync(path.join(fixtures,`desktop-build.${name}.json`),'utf8'));
      const file=path.join(temporary,'record.json');fs.writeFileSync(file,canonical(source));
      assert.equal(readBuild(file).buildId,source.buildId);
      for(const schema of ['1','2',true,3]){
        const changed=structuredClone(source);changed.buildInputs.schemaVersion=schema;
        assert.throws(()=>{
          changed.buildId=crypto.createHash('sha256').update(canonical(changed.buildInputs)).digest('hex');
          fs.writeFileSync(file,canonical(changed));readBuild(file);
        });
      }
      if(name==='profile')for(const key of ['runtimeProfileId','runtimeProfileSha256','runtimeInventorySha256']){
        const changed=structuredClone(source);delete changed.buildInputs[key];
        fs.writeFileSync(file,canonical(changed));assert.throws(()=>readBuild(file));
      }
    }
    assert.equal(runtimeLocale('zh_CN'),'zh-CN');assert.equal(runtimeLocale('zh-Hans'),'zh-CN');
    for(const locale of ['en-US','fr-FR','ja-JP',''])assert.equal(runtimeLocale(locale),'en-US');
  } finally {fs.rmSync(temporary,{recursive:true});}
});
