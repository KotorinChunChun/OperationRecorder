{{!--
===============================================================================
 操作報告書テンプレート(ユーザー向けの手順書（操作前画像だけ）)  -Template user-before
===============================================================================
 全手順に images.before を使用する。画像が無い側をもう一方で補わない。
--}}
# {{caption}}操作手順書

{{#if caption}}
本書では{{caption}}の操作手順について説明する
{{#else}}
本書では操作手順について説明する
{{#endif}}

{{#if description}}
{{description}}

{{#endif}}
{{#each steps}}
## 手順 {{number}}.{{#if headline}} {{headline}}{{#endif}}

{{#if body}}
{{body}}

{{#endif}}
{{#if windowTitle}}
**画面:** {{windowTitle}}

{{#endif}}
{{#if images.before}}
{{images.before}}

{{#endif}}
{{#endeach}}
