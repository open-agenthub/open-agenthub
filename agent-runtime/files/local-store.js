'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { Transform } = require('node:stream');
const { pipeline } = require('node:stream/promises');

const FILE_ID = /^[a-f0-9]{32}$/;

class LocalFileError extends Error {
  constructor(code) {
    super(code);
    this.code = code;
  }
}

class LocalFileStore {
  constructor(options = {}) {
    this.root = path.resolve(options.root || '/workspace/.agenthub/files');
    this.fs = options.fs || fs;
  }

  async put(id, name, readable, maxBytes) {
    this.validateId(id);
    const safeName = this.safeName(name);
    if (!readable || typeof readable.pipe !== 'function') throw new LocalFileError('invalid_stream');
    if (!Number.isSafeInteger(maxBytes) || maxBytes < 0) throw new LocalFileError('invalid_size_limit');

    const directory = await this.directory(id, true);
    const partial = path.join(directory, '.partial');
    const destination = path.join(directory, safeName);
    await this.removeStalePartial(partial);

    let output;
    let bytes = 0;
    try {
      output = this.fs.createWriteStream(partial, { flags: 'wx', mode: 0o600 });
      const limiter = new Transform({
        transform(chunk, _encoding, callback) {
          bytes += chunk.length;
          callback(bytes > maxBytes ? new LocalFileError('file_too_large') : null, chunk);
        }
      });
      await pipeline(readable, limiter, output);
      const syncHandle = await this.fs.promises.open(partial, 'r+');
      try {
        await syncHandle.sync();
      } finally {
        await syncHandle.close();
      }
      await this.fs.promises.rename(partial, destination);
      await this.fs.promises.chmod(destination, 0o600);
      return { id, name: safeName, size: bytes };
    } catch (error) {
      if (output) output.destroy();
      await this.fs.promises.rm(partial, { force: true }).catch(() => {});
      throw error;
    }
  }

  async open(id) {
    const metadata = await this.head(id);
    if (!metadata) return null;
    const flags = this.fs.constants.O_RDONLY | (this.fs.constants.O_NOFOLLOW || 0);
    let handle;
    try {
      handle = await this.fs.promises.open(metadata.path, flags);
      const stat = await handle.stat();
      if (!stat.isFile()) throw new LocalFileError('managed_root_escape');
      return {
        ...metadata,
        stream: handle.createReadStream({ autoClose: true })
      };
    } catch (error) {
      if (handle) await handle.close().catch(() => {});
      if (error.code === 'ELOOP') throw new LocalFileError('managed_root_escape');
      throw error;
    }
  }

  async head(id) {
    this.validateId(id);
    const directory = await this.directory(id, false);
    if (!directory) return null;
    const entries = await this.fs.promises.readdir(directory, { withFileTypes: true });
    const files = entries.filter(entry => entry.name !== '.partial');
    if (files.length === 0) return null;
    if (files.length !== 1 || !files[0].isFile() || files[0].isSymbolicLink())
      throw new LocalFileError('managed_root_escape');

    const filePath = path.join(directory, files[0].name);
    const realPath = await this.fs.promises.realpath(filePath);
    this.assertWithin(await this.fs.promises.realpath(this.root), realPath);
    const stat = await this.fs.promises.stat(realPath);
    if (!stat.isFile()) throw new LocalFileError('managed_root_escape');
    return { id, name: files[0].name, size: stat.size, path: realPath };
  }

  async remove(id) {
    this.validateId(id);
    await this.ensureRoot();
    const candidate = path.join(this.root, id);
    let stat;
    try {
      stat = await this.fs.promises.lstat(candidate);
    } catch (error) {
      if (error.code === 'ENOENT') return;
      throw error;
    }
    if (stat.isSymbolicLink() || !stat.isDirectory())
      throw new LocalFileError('managed_root_escape');
    const realRoot = await this.fs.promises.realpath(this.root);
    const realDirectory = await this.fs.promises.realpath(candidate);
    this.assertWithin(realRoot, realDirectory);
    await this.fs.promises.rm(realDirectory, { recursive: true });
  }

  validateId(id) {
    if (typeof id !== 'string' || !FILE_ID.test(id)) throw new LocalFileError('invalid_file_id');
  }

  safeName(name) {
    if (typeof name !== 'string' || !name.trim() || name.length > 255 || name.includes('\0'))
      throw new LocalFileError('invalid_file_name');
    const normalized = path.win32.basename(path.posix.basename(name.trim()));
    if (!normalized || normalized === '.' || normalized === '..' || /[\x00-\x1f]/.test(normalized))
      throw new LocalFileError('invalid_file_name');
    return normalized;
  }

  async ensureRoot() {
    await this.fs.promises.mkdir(this.root, { recursive: true, mode: 0o700 });
    const stat = await this.fs.promises.lstat(this.root);
    if (stat.isSymbolicLink() || !stat.isDirectory()) throw new LocalFileError('managed_root_escape');
    return this.fs.promises.realpath(this.root);
  }

  async directory(id, create) {
    this.validateId(id);
    const realRoot = await this.ensureRoot();
    const candidate = path.join(this.root, id);
    let stat;
    try {
      stat = await this.fs.promises.lstat(candidate);
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
      if (!create) return null;
      await this.fs.promises.mkdir(candidate, { mode: 0o700 });
      stat = await this.fs.promises.lstat(candidate);
    }
    if (stat.isSymbolicLink() || !stat.isDirectory()) throw new LocalFileError('managed_root_escape');
    const realDirectory = await this.fs.promises.realpath(candidate);
    this.assertWithin(realRoot, realDirectory);
    return realDirectory;
  }

  assertWithin(root, candidate) {
    const relative = path.relative(root, candidate);
    if (!relative || relative.startsWith(`..${path.sep}`) || relative === '..' || path.isAbsolute(relative)) {
      if (!relative) return;
      throw new LocalFileError('managed_root_escape');
    }
  }

  async removeStalePartial(partial) {
    try {
      const stat = await this.fs.promises.lstat(partial);
      if (stat.isSymbolicLink() || !stat.isFile()) throw new LocalFileError('managed_root_escape');
      await this.fs.promises.rm(partial);
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
    }
  }
}

module.exports = { LocalFileStore, LocalFileError, FILE_ID };
