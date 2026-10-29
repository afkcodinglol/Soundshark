export const MAX_AUDIO_BYTES = 10 * 1024 * 1024;

// Parse every MPEG Layer III frame; filenames and MIME headers are not trusted.
export function inspectMp3(bytes) {
  if (!(bytes instanceof Uint8Array) || !bytes.length || bytes.length > MAX_AUDIO_BYTES) throw new Error('Invalid audio size');
  let position = 0, duration = 0, frames = 0;
  if (bytes.length >= 10 && bytes[0] === 73 && bytes[1] === 68 && bytes[2] === 51) {
    if (bytes[3] < 2 || bytes[3] > 4 || bytes.slice(6,10).some(b => b & 128)) throw new Error('Invalid ID3 tag');
    const size = (bytes[6] << 21) | (bytes[7] << 14) | (bytes[8] << 7) | bytes[9];
    position = 10 + size + ((bytes[3] === 4 && (bytes[5] & 16)) ? 10 : 0);
    if (position > bytes.length) throw new Error('Truncated ID3 tag');
  }
  while (position < bytes.length) {
    if (bytes.length - position === 128 && bytes[position] === 84 && bytes[position+1] === 65 && bytes[position+2] === 71) { position += 128; break; }
    if (position + 4 > bytes.length) throw new Error('Truncated MP3 header');
    const a = bytes[position], b = bytes[position+1], c = bytes[position+2];
    const version = (b >> 3) & 3, layer = (b >> 1) & 3, rateIndex = (c >> 2) & 3, bitIndex = c >> 4;
    if (a !== 255 || (b & 224) !== 224 || version === 1 || layer !== 1 || rateIndex === 3 || bitIndex === 0 || bitIndex === 15) throw new Error('Invalid MP3 frame');
    const sampleRate = [44100,48000,32000][rateIndex] / (version === 3 ? 1 : version === 2 ? 2 : 4);
    const bitRate = (version === 3 ? [0,32,40,48,56,64,80,96,112,128,160,192,224,256,320] : [0,8,16,24,32,40,48,56,64,80,96,112,128,144,160])[bitIndex] * 1000;
    const size = Math.floor((version === 3 ? 144 : 72) * bitRate / sampleRate) + ((c >> 1) & 1);
    if (size < 4 || position + size > bytes.length) throw new Error('Truncated MP3 frame');
    duration += (version === 3 ? 1152 : 576) / sampleRate; frames++; position += size;
    if (duration > 60.1) throw new Error('Audio exceeds 60 seconds');
  }
  if (!frames || position !== bytes.length) throw new Error('No valid MP3 audio');
  return { duration, bytes: bytes.length };
}
