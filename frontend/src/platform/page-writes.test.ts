import {afterEach,describe,expect,it,vi} from 'vitest';
import {api} from './api';
import {beginPageWrite,pageWritesBusy,preparePageClose,consumePageClose,releasePageClose} from './page-writes';
import {enterPage} from './page-state';
import {browserPageRefresh,receiveBrowserPageRefresh,confirmBrowserPageRefresh,dismissPageRefresh} from './page-refresh';

describe('management page write protection',()=>{
  afterEach(()=>{releasePageClose();dismissPageRefresh();vi.restoreAllMocks();vi.unstubAllGlobals();expect(pageWritesBusy()).toBe(false);});
  it('counts pending writes through their response and never aborts them for refresh',async()=>{
    let finish!: (response: Response)=>void;
    const fetch=vi.spyOn(globalThis,'fetch').mockImplementation(()=>new Promise(resolve=>{finish=resolve;}));
    const write=api('PUT','/api/settings',{historyRetentionDays:9},new AbortController().signal);
    expect(pageWritesBusy()).toBe(true);
    expect(preparePageClose('lease',Date.now()+1000)).toBe(false);
    receiveBrowserPageRefresh({requestId:'a'.repeat(32),expiresAt:Date.now()+1000});
    expect(browserPageRefresh.value).toBeNull();
    expect(fetch.mock.calls[0][1]?.signal?.aborted).toBe(false);
    finish(new Response('{"ok":true}'));await write;
    expect(pageWritesBusy()).toBe(false);
  });
  it('holds a close permit across save and consume and rejects writes until release',async()=>{
    expect(preparePageClose('lease',Date.now()+1000)).toBe(true);
    await expect(api('POST','/api/plugins/game-check-in/tasks',{})).rejects.toThrow('page_close_pending');
    expect(consumePageClose('lease')).toBe(true);
    releasePageClose('lease');
    const release=beginPageWrite();expect(pageWritesBusy()).toBe(true);release();
    expect(consumePageClose('lease')).toBe(false);
  });
  it('rechecks writes and navigation after the browser confirmation was shown',()=>{
    enterPage('settings');
    const reload=vi.fn();vi.stubGlobal('location',{reload});
    receiveBrowserPageRefresh({requestId:'b'.repeat(32),expiresAt:Date.now()+1000});
    expect(browserPageRefresh.value).not.toBeNull();
    const release=beginPageWrite();confirmBrowserPageRefresh();release();
    expect(reload).not.toHaveBeenCalled();
    receiveBrowserPageRefresh({requestId:'c'.repeat(32),expiresAt:Date.now()+1000});
    enterPage('plugins');confirmBrowserPageRefresh();
    expect(reload).not.toHaveBeenCalled();
  });
});
