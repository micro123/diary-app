import crypto from 'node:crypto';
import { execFile } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { promisify } from 'node:util';
import { deflateSync, inflateSync } from 'node:zlib';
import { fileURLToPath } from 'node:url';

const pngSignature = Buffer.from('89504e470d0a1a0a', 'hex');
const pixelsPerMeter96Dpi = 3780;
const crcTable = buildCrcTable();
const execFileAsync = promisify(execFile);
const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const windowsCaptureScript = path.join(scriptDirectory, 'ui-window-screenshot.ps1');

function buildCrcTable() {
    const table = new Uint32Array(256);
    for (let value = 0; value < table.length; value++) {
        let crc = value;
        for (let bit = 0; bit < 8; bit++)
            crc = (crc & 1) ? 0xedb88320 ^ (crc >>> 1) : crc >>> 1;
        table[value] = crc >>> 0;
    }
    return table;
}

function crc32(buffer) {
    let crc = 0xffffffff;
    for (const value of buffer)
        crc = crcTable[(crc ^ value) & 0xff] ^ (crc >>> 8);
    return (crc ^ 0xffffffff) >>> 0;
}

function pngChunk(type, data) {
    const typeBuffer = Buffer.from(type, 'ascii');
    const output = Buffer.allocUnsafe(data.length + 12);
    output.writeUInt32BE(data.length, 0);
    typeBuffer.copy(output, 4);
    data.copy(output, 8);
    output.writeUInt32BE(crc32(Buffer.concat([typeBuffer, data])), data.length + 8);
    return output;
}

function readChunks(buffer) {
    if (buffer.length < 33 || !buffer.subarray(0, pngSignature.length).equals(pngSignature))
        throw new Error('截图不是有效的 PNG 文件');
    const chunks = [];
    let offset = pngSignature.length;
    while (offset + 12 <= buffer.length) {
        const length = buffer.readUInt32BE(offset);
        const end = offset + length + 12;
        if (end > buffer.length)
            throw new Error('PNG 数据块长度越界');
        const type = buffer.toString('ascii', offset + 4, offset + 8);
        chunks.push({ type, data: buffer.subarray(offset + 8, offset + 8 + length) });
        offset = end;
        if (type === 'IEND')
            break;
    }
    return chunks;
}

export function readPngInfo(buffer) {
    const header = readChunks(buffer).find(chunk => chunk.type === 'IHDR')?.data;
    if (!header || header.length !== 13)
        throw new Error('PNG 缺少有效 IHDR');
    return {
        width: header.readUInt32BE(0),
        height: header.readUInt32BE(4),
        bitDepth: header[8],
        colorType: header[9],
        compression: header[10],
        filter: header[11],
        interlace: header[12],
    };
}

function paeth(left, above, upperLeft) {
    const estimate = left + above - upperLeft;
    const leftDistance = Math.abs(estimate - left);
    const aboveDistance = Math.abs(estimate - above);
    const upperLeftDistance = Math.abs(estimate - upperLeft);
    if (leftDistance <= aboveDistance && leftDistance <= upperLeftDistance)
        return left;
    return aboveDistance <= upperLeftDistance ? above : upperLeft;
}

function decodeRgbaPng(buffer) {
    const chunks = readChunks(buffer);
    const info = readPngInfo(buffer);
    if (info.bitDepth !== 8 || ![2, 6].includes(info.colorType)
        || info.compression !== 0 || info.filter !== 0 || info.interlace !== 0) {
        throw new Error('只支持 CDP 输出的 8-bit、非交错 RGB/RGBA PNG');
    }
    const bytesPerPixel = info.colorType === 6 ? 4 : 3;
    const rowBytes = info.width * bytesPerPixel;
    const compressed = Buffer.concat(chunks.filter(chunk => chunk.type === 'IDAT').map(chunk => chunk.data));
    const filtered = inflateSync(compressed);
    const expectedLength = (rowBytes + 1) * info.height;
    if (filtered.length !== expectedLength)
        throw new Error(`PNG 解压尺寸异常：期望 ${expectedLength}，实际 ${filtered.length}`);

    const decoded = Buffer.allocUnsafe(rowBytes * info.height);
    let inputOffset = 0;
    for (let y = 0; y < info.height; y++) {
        const filterType = filtered[inputOffset++];
        const rowOffset = y * rowBytes;
        const previousOffset = rowOffset - rowBytes;
        for (let x = 0; x < rowBytes; x++) {
            const raw = filtered[inputOffset++];
            const left = x >= bytesPerPixel ? decoded[rowOffset + x - bytesPerPixel] : 0;
            const above = y > 0 ? decoded[previousOffset + x] : 0;
            const upperLeft = y > 0 && x >= bytesPerPixel
                ? decoded[previousOffset + x - bytesPerPixel]
                : 0;
            let predictor;
            switch (filterType) {
                case 0: predictor = 0; break;
                case 1: predictor = left; break;
                case 2: predictor = above; break;
                case 3: predictor = Math.floor((left + above) / 2); break;
                case 4: predictor = paeth(left, above, upperLeft); break;
                default: throw new Error(`不支持的 PNG 过滤器：${filterType}`);
            }
            decoded[rowOffset + x] = (raw + predictor) & 0xff;
        }
    }

    if (info.colorType === 6)
        return { width: info.width, height: info.height, pixels: decoded };
    const rgba = Buffer.allocUnsafe(info.width * info.height * 4);
    for (let source = 0, target = 0; source < decoded.length; source += 3, target += 4) {
        rgba[target] = decoded[source];
        rgba[target + 1] = decoded[source + 1];
        rgba[target + 2] = decoded[source + 2];
        rgba[target + 3] = 255;
    }
    return { width: info.width, height: info.height, pixels: rgba };
}

function resizeRgba(source, sourceWidth, sourceHeight, targetWidth, targetHeight) {
    if (sourceWidth === targetWidth && sourceHeight === targetHeight)
        return Buffer.from(source);
    const target = Buffer.allocUnsafe(targetWidth * targetHeight * 4);
    const scaleX = sourceWidth / targetWidth;
    const scaleY = sourceHeight / targetHeight;
    for (let y = 0; y < targetHeight; y++) {
        const sourceY = Math.max(0, Math.min(sourceHeight - 1, (y + 0.5) * scaleY - 0.5));
        const y0 = Math.floor(sourceY);
        const y1 = Math.min(sourceHeight - 1, y0 + 1);
        const yWeight = sourceY - y0;
        for (let x = 0; x < targetWidth; x++) {
            const sourceX = Math.max(0, Math.min(sourceWidth - 1, (x + 0.5) * scaleX - 0.5));
            const x0 = Math.floor(sourceX);
            const x1 = Math.min(sourceWidth - 1, x0 + 1);
            const xWeight = sourceX - x0;
            const topLeft = (y0 * sourceWidth + x0) * 4;
            const topRight = (y0 * sourceWidth + x1) * 4;
            const bottomLeft = (y1 * sourceWidth + x0) * 4;
            const bottomRight = (y1 * sourceWidth + x1) * 4;
            const output = (y * targetWidth + x) * 4;
            for (let channel = 0; channel < 4; channel++) {
                const top = source[topLeft + channel] * (1 - xWeight)
                    + source[topRight + channel] * xWeight;
                const bottom = source[bottomLeft + channel] * (1 - xWeight)
                    + source[bottomRight + channel] * xWeight;
                target[output + channel] = Math.round(top * (1 - yWeight) + bottom * yWeight);
            }
        }
    }
    return target;
}

export function encodeRgbaPng(width, height, pixels) {
    if (!Number.isInteger(width) || !Number.isInteger(height) || width <= 0 || height <= 0)
        throw new Error('PNG 尺寸必须是正整数');
    if (pixels.length !== width * height * 4)
        throw new Error('RGBA 像素数量与 PNG 尺寸不匹配');

    const header = Buffer.alloc(13);
    header.writeUInt32BE(width, 0);
    header.writeUInt32BE(height, 4);
    header[8] = 8;
    header[9] = 6;
    const physical = Buffer.alloc(9);
    physical.writeUInt32BE(pixelsPerMeter96Dpi, 0);
    physical.writeUInt32BE(pixelsPerMeter96Dpi, 4);
    physical[8] = 1;
    const scanlines = Buffer.allocUnsafe((width * 4 + 1) * height);
    for (let y = 0; y < height; y++) {
        const targetOffset = y * (width * 4 + 1);
        scanlines[targetOffset] = 0;
        pixels.copy(scanlines, targetOffset + 1, y * width * 4, (y + 1) * width * 4);
    }
    return Buffer.concat([
        pngSignature,
        pngChunk('IHDR', header),
        pngChunk('pHYs', physical),
        pngChunk('IDAT', deflateSync(scanlines, { level: 6 })),
        pngChunk('IEND', Buffer.alloc(0)),
    ]);
}

export function normalizePngScreenshot(buffer, targetWidth, targetHeight) {
    const decoded = decodeRgbaPng(buffer);
    const pixels = resizeRgba(decoded.pixels, decoded.width, decoded.height, targetWidth, targetHeight);
    return encodeRgbaPng(targetWidth, targetHeight, pixels);
}

function viewportSize(metrics) {
    const viewport = metrics.cssVisualViewport ?? metrics.visualViewport
        ?? metrics.cssLayoutViewport ?? metrics.layoutViewport;
    const cssWidth = viewport?.width ?? viewport?.clientWidth ?? 0;
    const cssHeight = viewport?.height ?? viewport?.clientHeight ?? 0;
    const width = Math.round(cssWidth);
    const height = Math.round(cssHeight);
    if (width <= 0 || height <= 0)
        throw new Error('CDP 未返回有效的逻辑视口尺寸');
    return { width, height, cssWidth, cssHeight };
}

function digest(buffer) {
    return crypto.createHash('sha256').update(buffer).digest('hex');
}

async function captureWindowsWindow(processId, outputPath, timeoutMs) {
    const { stdout } = await execFileAsync('pwsh', [
        '-NoLogo',
        '-NoProfile',
        '-NonInteractive',
        '-File', windowsCaptureScript,
        '-TargetProcessId', String(processId),
        '-OutputPath', outputPath,
    ], {
        timeout: Math.max(timeoutMs, 30000),
        windowsHide: true,
        maxBuffer: 1024 * 1024,
    });
    const result = JSON.parse(stdout.trim());
    const buffer = await fs.readFile(outputPath);
    return { buffer, result };
}

export async function captureUiScreenshot({
    client,
    repositoryRoot,
    fileName,
    processId,
    timeoutMs = 15000,
    keepPhysical = true,
}) {
    const started = performance.now();
    const metrics = await client.send('Page.getLayoutMetrics', {}, timeoutMs);
    const logical = viewportSize(metrics);
    const screenshotRoot = path.join(repositoryRoot, '.build-tmp', 'ui-test', 'screenshots');
    const physicalPath = path.join(screenshotRoot, 'raw-physical', fileName);
    await fs.mkdir(path.dirname(physicalPath), { recursive: true });
    let physicalBuffer;
    let captureSource;
    if (process.platform === 'win32' && processId) {
        const native = await captureWindowsWindow(processId, physicalPath, timeoutMs);
        physicalBuffer = native.buffer;
        captureSource = 'windows-print-window';
    }
    else {
        const result = await client.send('Page.captureScreenshot', { format: 'png' }, timeoutMs);
        physicalBuffer = Buffer.from(result.data, 'base64');
        captureSource = 'cdp';
    }
    const physical = readPngInfo(physicalBuffer);
    const normalized = physical.width !== logical.width || physical.height !== logical.height;
    const outputBuffer = normalized
        ? normalizePngScreenshot(physicalBuffer, logical.width, logical.height)
        : physicalBuffer;
    const outputPath = path.join(screenshotRoot, fileName);
    await fs.mkdir(path.dirname(outputPath), { recursive: true });
    await fs.writeFile(outputPath, outputBuffer);

    let retainedPhysicalPath;
    if (normalized && keepPhysical) {
        retainedPhysicalPath = physicalPath;
        if (captureSource === 'cdp')
            await fs.writeFile(physicalPath, physicalBuffer);
    }
    else if (captureSource === 'windows-print-window') {
        await fs.unlink(physicalPath);
    }
    const renderScale = Number(Math.max(
        physical.width / logical.cssWidth,
        physical.height / logical.cssHeight).toFixed(4));
    return {
        elapsedMs: performance.now() - started,
        bytes: outputBuffer.length,
        sha256: digest(outputBuffer),
        path: outputPath,
        width: logical.width,
        height: logical.height,
        dpi: 96,
        captureSource,
        normalized,
        renderScale,
        physicalWidth: physical.width,
        physicalHeight: physical.height,
        ...(retainedPhysicalPath ? {
            physicalPath: retainedPhysicalPath,
            physicalBytes: physicalBuffer.length,
            physicalSha256: digest(physicalBuffer),
        } : {}),
    };
}
