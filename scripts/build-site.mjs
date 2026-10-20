import {mkdir,readFile,writeFile,copyFile,readdir} from 'node:fs/promises';
import {resolve,dirname,posix} from 'node:path';
import {fileURLToPath} from 'node:url';
import {execFileSync} from 'node:child_process';
import {createHash} from 'node:crypto';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'..'),source=resolve(root,'site'),out=resolve(source,'dist');
const project=JSON.parse(await readFile(resolve(source,'firebase.json'),'utf8')).hosting.site;
const logo=project==='soft-trace'?'src/SoftTrace.App/Assets/SoftTrace-Source.png':'icons/timer-128.png';
async function walk(directory,prefix=''){const files=[];for(const e of await readdir(directory,{withFileTypes:true})){if(e.name.startsWith('.')||(!prefix&&['dist','firebase.json'].includes(e.name)))continue;const path=prefix+e.name;if(e.isDirectory())files.push(...await walk(resolve(directory,e.name),path+'/'));else{if(!/\.(html|css|js|mjs|json|png|svg|ico)$/.test(e.name))throw Error('Unexpected public input: '+path);files.push(path);}}return files;}
const files=await walk(source);
await mkdir(out,{recursive:true});
for(const name of files){const path=resolve(out,name);if(!path.startsWith(out+'/')&&!path.startsWith(out+'\\'))throw Error('Invalid output path');await mkdir(dirname(path),{recursive:true});await copyFile(resolve(source,name),path);}
await copyFile(resolve(root,logo),resolve(out,'logo.png'));
if(!files.includes('logo.png'))files.push('logo.png');
const names=new Set(files);
for(const name of files.filter(f=>f.endsWith('.html'))){const html=await readFile(resolve(out,name),'utf8');if(!/<html[^>]*lang="zh-CN"/.test(html)||!html.includes('name="viewport"'))throw Error('Missing document metadata: '+name);for(const m of html.matchAll(/(?:href|src)="([^"]+)"/g)){const url=m[1];if(!url||/^(?:https?:|data:|#|\/\/)/.test(url))continue;const link=new URL(url,'https://website.invalid/'+name);let path=decodeURIComponent(link.pathname).slice(1);if(!path||path.endsWith('/'))path+='index.html';if(!names.has(path))throw Error('Missing local resource: '+name+' -> '+url);}}
for(const name of files.filter(f=>/\.(m?js)$/.test(f)))execFileSync(process.execPath,['--check',resolve(out,name)],{stdio:'pipe'});
const sourceCommit=execFileSync('git',['rev-parse','HEAD'],{cwd:root,encoding:'utf8'}).trim(),hashes={};
for(const name of files)hashes[name]=createHash('sha256').update(await readFile(resolve(out,name))).digest('hex');
await writeFile(resolve(out,'release.json'),JSON.stringify({project,sourceCommit,files:hashes},null,2)+'\n');
const policy="default-src 'self'; script-src 'self' https://www.gstatic.com https://apis.google.com; style-src 'self' 'unsafe-inline'; img-src 'self' data: https://*.googleusercontent.com; connect-src 'self' https://identitytoolkit.googleapis.com https://securetoken.googleapis.com https://firestore.googleapis.com https://www.googleapis.com https://www.gstatic.com https://*.firebaseapp.com; frame-src https://*.firebaseapp.com https://*.web.app https://accounts.google.com; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
const headers=[{key:'X-Content-Type-Options',value:'nosniff'},{key:'Referrer-Policy',value:'strict-origin-when-cross-origin'},{key:'Content-Security-Policy',value:policy},{key:'Cross-Origin-Opener-Policy',value:'same-origin-allow-popups'}];
await writeFile(resolve(out,'_headers'),'/*\n'+headers.map(h=>'  '+h.key+': '+h.value).join('\n')+'\n');
await writeFile(resolve(out,'vercel.json'),JSON.stringify({version:2,framework:null,buildCommand:null,outputDirectory:'.',headers:[{source:'/(.*)',headers}]},null,2)+'\n');
console.log(JSON.stringify({project,sourceCommit,output:out,files:files.length+3},null,2));
