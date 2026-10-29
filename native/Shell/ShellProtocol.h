#pragma once
#include "ShellCommon.h"
namespace fh {
struct Row {std::wstring token,label,name,modified;uint64_t modifiedTicks{};uint32_t flags{};};
struct Reply {uint32_t status=4;std::wstring session,summary,location,versionCount;bool restore{};std::vector<Row> rows;};
inline Reply ParseReply(const std::vector<uint8_t>& bytes){
 Reader r{bytes};auto operation=r.header();Check(operation==101||operation==103);auto table=operation==103;Reply result;result.status=r.u32();Check(result.status<=5);
 result.session=r.text(32);Check(result.session.empty()||Token(result.session));result.summary=r.text(4096);
 if(table)result.versionCount=r.text(1024);else result.location=r.text(32760);
 auto restore=r.u32(),count=r.u32();Check(restore<=1 && count<=(table?20u:5u) && (count==0||!result.session.empty()) && (restore==0||count>0));result.restore=restore!=0;
 for(uint32_t i=0;i<count;i++){Row row{};row.token=r.text(32);
  if(table){row.name=r.text(1024);row.modified=r.text(1024);row.modifiedTicks=r.u64();row.flags=r.u32();Check(row.modifiedTicks<=3155378975999999999ULL&&row.flags<=3);}
  else row.label=r.text(1024);
  Check(Token(row.token));for(const auto& previous:result.rows)Check(previous.token!=row.token);result.rows.push_back(std::move(row));}
 r.end();return result;
}
}
