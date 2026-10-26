#pragma once
#include "ShellCommon.h"
namespace fh {
struct Row {std::wstring token,label;};
struct Reply {uint32_t status=4;std::wstring session,summary,location;bool restore{};std::vector<Row> rows;};
inline Reply ParseReply(const std::vector<uint8_t>& bytes){
 Reader r{bytes};Check(r.header()==101);Reply result;result.status=r.u32();Check(result.status<=5);
 result.session=r.text(32);Check(result.session.empty()||Token(result.session));result.summary=r.text(4096);result.location=r.text(32760);
 auto restore=r.u32(),count=r.u32();Check(restore<=1 && count<=5 && (count==0||!result.session.empty()) && (restore==0||count>0));result.restore=restore!=0;
 for(uint32_t i=0;i<count;i++){Row row{r.text(32),r.text(1024)};Check(Token(row.token));for(const auto& previous:result.rows)Check(previous.token!=row.token);result.rows.push_back(std::move(row));}
 r.end();return result;
}
}
