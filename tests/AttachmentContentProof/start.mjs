import fs from 'node:fs';
import { spawn } from 'node:child_process';

// A distinct public endpoint reproduces a pod unable to reach its presigned storage URL,
// while the real backend still reads and writes through its internal storage endpoint.
const key = fs.readFileSync(process.env.PROOF_S3_KEY_FILE, 'utf8');
const env = { ...process.env,
  ConnectionStrings__Postgres: process.env.PROOF_POSTGRES || 'Host=127.0.0.1;Port=54329;Username=postgres;Database=postgres',
  S3__ServiceUrl: 'http://127.0.0.1:3910', S3__PublicUrl: 'http://127.0.0.1:3919',
  S3__Region: 'local', S3__Bucket: 'file-proof',
  S3__AccessKey: key.match(/Key ID:\s*(\S+)/)[1], S3__SecretKey: key.match(/Secret key:\s*(\S+)/i)[1],
  DOTNET_SYSTEM_GLOBALIZATION_INVARIANT: '1', DOTNET_GCHeapHardLimit: '0x30000000'
};
const child = spawn(process.env.DOTNET || 'dotnet',
  ['tests/FileTypesProof/bin/Debug/net10.0/Proof.dll'], { env, stdio: 'inherit' });
process.on('SIGTERM', () => child.kill());
child.on('exit', code => { process.exitCode = code ?? 1; });
