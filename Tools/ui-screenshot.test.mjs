import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {
    captureUiScreenshot,
    encodeRgbaPng,
    normalizePngScreenshot,
    readPngInfo,
} from './ui-screenshot.mjs';

function samplePixels(width, height) {
    const pixels = Buffer.alloc(width * height * 4);
    for (let y = 0; y < height; y++) {
        for (let x = 0; x < width; x++) {
            const offset = (y * width + x) * 4;
            pixels[offset] = x * 30;
            pixels[offset + 1] = y * 40;
            pixels[offset + 2] = 160;
            pixels[offset + 3] = 255;
        }
    }
    return pixels;
}

test('normalizePngScreenshot 输出指定的逻辑尺寸', () => {
    const source = encodeRgbaPng(6, 6, samplePixels(6, 6));
    const normalized = normalizePngScreenshot(source, 4, 4);
    assert.deepEqual(readPngInfo(normalized), {
        width: 4,
        height: 4,
        bitDepth: 8,
        colorType: 6,
        compression: 0,
        filter: 0,
        interlace: 0,
    });
});

test('captureUiScreenshot 保留物理图并输出 96 DPI 逻辑图', async () => {
    const repositoryRoot = await fs.mkdtemp(path.join(os.tmpdir(), 'diary-ui-screenshot-'));
    const physical = encodeRgbaPng(6, 6, samplePixels(6, 6));
    const client = {
        async send(method) {
            if (method === 'Page.getLayoutMetrics')
                return { cssVisualViewport: { width: 4, height: 4 } };
            if (method === 'Page.captureScreenshot')
                return { data: physical.toString('base64') };
            throw new Error('意外 CDP 命令：' + method);
        },
    };
    try {
        const result = await captureUiScreenshot({ client, repositoryRoot, fileName: 'manual/test.png' });
        assert.equal(result.normalized, true);
        assert.equal(result.renderScale, 1.5);
        assert.equal(result.width, 4);
        assert.equal(result.height, 4);
        assert.equal(result.physicalWidth, 6);
        assert.equal(result.physicalHeight, 6);
        assert.equal(readPngInfo(await fs.readFile(result.path)).width, 4);
        assert.equal(readPngInfo(await fs.readFile(result.physicalPath)).width, 6);
    }
    finally {
        await fs.rm(repositoryRoot, { recursive: true, force: true });
    }
});

test('captureUiScreenshot 根据窗口像素自动识别非 150% 缩放', async () => {
    const repositoryRoot = await fs.mkdtemp(path.join(os.tmpdir(), 'diary-ui-screenshot-scale-'));
    const physical = encodeRgbaPng(10, 10, samplePixels(10, 10));
    const client = {
        async send(method) {
            if (method === 'Page.getLayoutMetrics')
                return { cssVisualViewport: { width: 8, height: 8 } };
            if (method === 'Page.captureScreenshot')
                return { data: physical.toString('base64') };
            throw new Error('意外 CDP 命令：' + method);
        },
    };
    try {
        const result = await captureUiScreenshot({ client, repositoryRoot, fileName: 'scale-125.png' });
        assert.equal(result.renderScale, 1.25);
        assert.equal(result.width, 8);
        assert.equal(result.height, 8);
        assert.equal(result.captureSource, 'cdp');
    }
    finally {
        await fs.rm(repositoryRoot, { recursive: true, force: true });
    }
});
