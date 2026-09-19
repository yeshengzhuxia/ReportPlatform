import {test} from 'node:test';
import assert from 'node:assert/strict';
import {hashPassword,verifyPassword,encrypt,decrypt,validateSQL,buildQuery} from '../server/security.mjs';
test('密码哈希及错误密码拒绝',()=>{const h=hashPassword('LongPassword123!');assert(verifyPassword('LongPassword123!',h));assert(!verifyPassword('WrongPassword123!',h));assert.throws(()=>hashPassword('short'))});
test('数据库密码加密和篡改检测',()=>{const key=Buffer.alloc(32,1),v=encrypt('secret',key);assert.equal(decrypt(v,key),'secret');assert.throws(()=>decrypt(v,Buffer.alloc(32,2)))});
test('拒绝 SQL 写入、堆叠和注释',()=>{for(const q of ['DELETE FROM users','SELECT * INTO copied FROM users','SELECT 1; DROP TABLE users','SELECT 1 -- comment','SELECT * FROM OPENROWSET(x)'])assert.throws(()=>validateSQL(q))});
test('组织隔离和筛选值强制参数化',()=>{const r={sql:'SELECT OrgId, Amount FROM dbo.Orders',orgColumn:'OrgId',fields:[{key:'Amount'}]};const q=buildQuery(r,"1' OR 1=1",[{field:'Amount',op:'eq',value:"0' OR 1=1"}],999999);assert.match(q.sql,/TOP \(10001\)/);assert.match(q.sql,/\[OrgId\] = @org/);assert(!q.sql.includes('OR 1=1'));assert.equal(q.params[0].value,"1' OR 1=1");assert.throws(()=>buildQuery(r,'1',[{field:'Unknown',op:'eq',value:1}]))});
test('LIKE 字面量转义',()=>{const q=buildQuery({sql:'SELECT OrgId, Name FROM T',orgColumn:'OrgId',fields:[{key:'Name'}]},'1',[{field:'Name',op:'contains',value:'a%b_'}]);assert.equal(q.params[1].value,'%a~%b~_%')});
