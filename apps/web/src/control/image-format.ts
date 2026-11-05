export interface ImageFormat {
  readonly type: 'image/png' | 'image/jpeg' | 'image/webp';
  readonly width: number;
  readonly height: number;
}

export function imageFormat(bytes: Uint8Array, contentType: string | null): ImageFormat {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let result: ImageFormat | undefined;
  if (bytes.length >= 33 && view.getUint32(0) === 0x89504e47 && view.getUint32(4) === 0x0d0a1a0a &&
      view.getUint32(8) === 13 && view.getUint32(12) === 0x49484452) {
    result = { type: 'image/png', width: view.getUint32(16), height: view.getUint32(20) };
  } else if (bytes.length >= 12 && view.getUint16(0) === 0xffd8) {
    result = jpegFormat(bytes, view);
  } else if (bytes.length >= 20 && view.getUint32(0) === 0x52494646 && view.getUint32(8) === 0x57454250 &&
      view.getUint32(4, true) === bytes.length - 8) {
    result = webpFormat(bytes, view);
  }
  if (!result || contentType?.split(';')[0].trim().toLowerCase() !== result.type)
    throw new Error('image_format_invalid');
  if (!result.width || !result.height || result.width > 8192 || result.height > 8192)
    throw new Error('image_dimensions_invalid');
  return result;
}

function jpegFormat(bytes: Uint8Array, view: DataView): ImageFormat | undefined {
  let offset = 2;
  while (offset < bytes.length) {
    if (bytes[offset++] !== 0xff) return undefined;
    while (offset < bytes.length && bytes[offset] === 0xff) offset++;
    const marker = bytes[offset++];
    if (marker === undefined || marker === 0xda || marker === 0xd9 || marker === 0) return undefined;
    if (marker === 0x01 || marker >= 0xd0 && marker <= 0xd7) continue;
    if (offset + 2 > bytes.length) return undefined;
    const length = view.getUint16(offset);
    if (length < 2 || offset + length > bytes.length) return undefined;
    if ([0xc0, 0xc1, 0xc2, 0xc3, 0xc5, 0xc6, 0xc7, 0xc9, 0xca, 0xcb, 0xcd, 0xce, 0xcf].includes(marker)) {
      if (length < 8) return undefined;
      return { type: 'image/jpeg', width: view.getUint16(offset + 5), height: view.getUint16(offset + 3) };
    }
    offset += length;
  }
  return undefined;
}

function webpFormat(bytes: Uint8Array, view: DataView): ImageFormat | undefined {
  const length = view.getUint32(16, true);
  if (length > bytes.length - 20) return undefined;
  const chunk = view.getUint32(12);
  if (chunk === 0x56503858 && length === 10 && bytes.length >= 30) {
    return { type: 'image/webp', width: 1 + bytes[24]! + bytes[25]! * 256 + bytes[26]! * 65536,
      height: 1 + bytes[27]! + bytes[28]! * 256 + bytes[29]! * 65536 };
  }
  if (chunk === 0x5650384c && length >= 5 && bytes[20] === 0x2f) {
    const dimensions = view.getUint32(21, true);
    return { type: 'image/webp', width: 1 + (dimensions & 0x3fff), height: 1 + ((dimensions >>> 14) & 0x3fff) };
  }
  if (chunk === 0x56503820 && length >= 10 && !(bytes[20]! & 1) &&
      bytes[23] === 0x9d && bytes[24] === 1 && bytes[25] === 0x2a) {
    return { type: 'image/webp', width: view.getUint16(26, true) & 0x3fff, height: view.getUint16(28, true) & 0x3fff };
  }
  return undefined;
}
