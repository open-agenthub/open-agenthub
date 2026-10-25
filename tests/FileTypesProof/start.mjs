import fs from 'node:fs';
import { spawn } from 'node:child_process';
// Read local proof credentials without putting them in command lines or logs.
const key = fs.readFileSync(process.env.PROOF_S3_KEY_FILE, 'utf8');
const env = { ...process.env,
  ConnectionStrings__Postgres: 'Host=127.0.0.1;Port=54329;Username=postgres;Database=postgres',
  S3__ServiceUrl: 'http://127.0.0.1:3910', S3__PublicUrl: 'http://127.0.0.1:3910', S3__Region: 'local', S3__Bucket: 'file-proof',
  S3__AccessKey: key.match(/Key ID:\s*(\S+)/)[1], S3__SecretKey: key.match(/Secret key:\s*(\S+)/i)[1],
  DOTNET_SYSTEM_GLOBALIZATION_INVARIANT: '1', DOTNET_GCHeapHardLimit: '0x30000000'
};
const child = spawn(process.env.DOTNET || 'dotnet', [process.env.PROOF_DLL || 'tests/FileTypesProof/bin/Debug/net10.0/Proof.dll'], { env, stdio: 'inherit' });
process.on('SIGTERM', () => child.kill());
