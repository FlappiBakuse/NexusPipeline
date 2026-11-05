import { readFileSync } from 'node:fs';
import { createPrivateKey, createPublicKey, sign, verify } from 'node:crypto';

const [keyPath, inputPath] = process.argv.slice(2);
if (!keyPath || !inputPath || process.argv.length !== 4) throw new Error('Explicit key and metadata paths required');
const bytes = readFileSync(keyPath);
const key = createPrivateKey({ key: bytes, format: 'der', type: 'pkcs8' });
if (key.asymmetricKeyType !== 'ec' || key.asymmetricKeyDetails?.namedCurve !== 'prime256v1') {
  throw new Error('P-256 signing key required');
}
const publicKey = createPublicKey(key);
const original = readFileSync(inputPath);
const signature = sign('sha256', original, { key, dsaEncoding: 'ieee-p1363' });
if (signature.length !== 64 || !verify('sha256', original, { key: publicKey, dsaEncoding: 'ieee-p1363' }, signature)) {
  throw new Error('Detached signature verification failed');
}
process.stdout.write(JSON.stringify({ signature: signature.toString('base64'),
  publicKey: publicKey.export({ type: 'spki', format: 'der' }).toString('base64') }));
