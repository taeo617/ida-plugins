// 팀원 추가: node scripts/add-member.cjs <email>   또는 도메인: node scripts/add-member.cjs @회사.com
// 이메일 원문은 저장하지 않고 sha256 해시만 agent/agent.json access 목록에 넣어요. 커밋·푸시는 직접.
const fs = require("fs"), path = require("path"), crypto = require("crypto");
const arg = (process.argv[2] || "").trim().toLowerCase();
if (!arg.includes("@")) { console.log("사용법: node scripts/add-member.cjs <email | @domain>"); process.exit(1); }
const file = path.join(__dirname, "..", "agent", "agent.json");
const data = JSON.parse(fs.readFileSync(file, "utf8"));
const a = data.access || (data.access = { version: "", allowedDomains: [], allowedEmailHashes: [], contact: "관리자에게 로그인한 이메일 주소를 알려 주세요." });
if (arg.startsWith("@")) { const d = arg.slice(1); if (!a.allowedDomains.includes(d)) a.allowedDomains.push(d); }
else { const h = crypto.createHash("sha256").update(arg).digest("hex"); if (!a.allowedEmailHashes.includes(h)) a.allowedEmailHashes.push(h); }
const today = new Date().toISOString().slice(0, 10);
const n = a.version.startsWith(today) ? Number(a.version.split(".").pop()) + 1 : 1;
a.version = `${today}.${n}`;
fs.writeFileSync(file, JSON.stringify(data, null, 2) + "\n");
console.log(`추가했어요. access ${a.version} · 도메인 ${a.allowedDomains.length}개 · 이메일 ${a.allowedEmailHashes.length}개`);
