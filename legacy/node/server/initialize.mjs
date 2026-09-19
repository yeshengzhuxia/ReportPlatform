import fs from 'node:fs';
import {randomBytes} from 'node:crypto';
if(fs.existsSync('.env')){console.log('现有配置已保留');process.exit(0)}
const password=randomBytes(18).toString('base64url');
fs.writeFileSync('.env',`PORT=3080\nHOST=127.0.0.1\nADMIN_PASSWORD=${password}\nENCRYPTION_KEY=${randomBytes(32).toString('hex')}\nCOOKIE_SECURE=false\n`,{mode:0o600});
fs.writeFileSync('首次登录.txt',`澄数报表查询平台\n\n访问地址：http://127.0.0.1:3080\n登录账号：admin\n初始密码：${password}\n账套：默认账套\n\n此密码随机生成，仅保存在本机。请妥善保管，登录后可在用户管理中重置密码。重置后删除本文件。\n`,{mode:0o600});
console.log('初始化完成，登录信息已保存到首次登录.txt');
