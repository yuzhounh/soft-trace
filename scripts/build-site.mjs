import {mkdir,readFile,writeFile,copyFile,readdir} from 'node:fs/promises';
import {resolve,dirname} from 'node:path';
import {fileURLToPath} from 'node:url';
import {execFileSync} from 'node:child_process';
import {createHash} from 'node:crypto';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
const source=resolve(root,'site');
const out=resolve(source,'dist');
await mkdir(out,{recursive:true});
const publicFiles=['index.html','privacy.html','site.css','logo.png','release.json'];
const configFiles=['_headers','vercel.json'];
for(const name of await readdir(out)){if(![...publicFiles,...configFiles,'.vercel'].includes(name))throw new Error('Unexpected output file: '+name);}
for(const name of ['index.html','privacy.html','site.css'])await copyFile(resolve(source,name),resolve(out,name));
await copyFile(resolve(root,'src/SoftTrace.App/Assets/SoftTrace-Source.png'),resolve(out,'logo.png'));
for(const name of ['index.html','privacy.html']){const html=await readFile(resolve(out,name),'utf8');if(!html.includes('<html lang="zh-CN">')||!html.includes('name="viewport"'))throw new Error('Missing document metadata');for(const m of html.matchAll(/(?:href|src)="([^"#]+)"/g)){const url=m[1].split('#')[0];if(url==='./'||/^https?:/.test(url))continue;if(!['site.css','logo.png','privacy.html'].includes(url))throw new Error('Unexpected local resource: '+url);await readFile(resolve(out,url));}}
const sourceCommit=execFileSync('git',['rev-parse','HEAD'],{cwd:root,encoding:'utf8'}).trim();
const hashes={};for(const name of publicFiles.filter(n=>n!=='release.json'))hashes[name]=createHash('sha256').update(await readFile(resolve(out,name))).digest('hex');
await writeFile(resolve(out,'release.json'),JSON.stringify({project:'soft-trace',sourceCommit,files:hashes},null,2)+'\n');
await writeFile(resolve(out,'_headers'),'/*\n  X-Content-Type-Options: nosniff\n  Referrer-Policy: strict-origin-when-cross-origin\n  X-Frame-Options: DENY\n  Content-Security-Policy: default-src \'none\'; style-src \'self\'; img-src \'self\'; base-uri \'self\'; form-action \'none\'; frame-ancestors \'none\'\n');
await writeFile(resolve(out,'vercel.json'),JSON.stringify({version:2,framework:null,buildCommand:null,outputDirectory:'.',headers:[{source:'/(.*)',headers:[{key:'X-Content-Type-Options',value:'nosniff'},{key:'Referrer-Policy',value:'strict-origin-when-cross-origin'}]}]},null,2)+'\n');
console.log(JSON.stringify({project:'soft-trace',sourceCommit,output:out,files:[...publicFiles,...configFiles]},null,2));
