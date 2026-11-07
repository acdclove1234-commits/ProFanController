ProFan Maru
===========
Zen Maru Gothic (Copyright 2021 The Zen Maru Gothic Project Authors,
https://github.com/googlefonts/zen-marugothic) を改変したフォントです。

改変内容:
  ・ひらがな・カタカナの文字幅を、字形の幅 + 左右 5.5% の余白に詰めた（プロポーショナル化）
  ・句読点・括弧・全角記号は、最低でも半角分の幅を残して詰めた
  ・SIL Open Font License 1.1 の慣例に従い、フォント名を「ProFan Maru」に変更した
  ・アプリを軽くするため、文字を絞った（1 つのファイル 3.7 MB → 1.7 MB）:
    英数字・記号・ひらがな・カタカナ・全角の文字、JIS 第 1 水準の漢字、アプリの画面と案内に出てくる文字だけを残した。
    ほかの文字は形を空にして文字の対応表から外したので、アプリでは Windows の Yu Gothic UI で表示される。
    グリフの番号は変えていない。作り直すときは、元のフォント（git の履歴の、絞る前の Assets/Fonts/ProFanMaru-*.ttf）から
    tools/FontSubset で作る:
      dotnet run --project tools/FontSubset -- <元のフォント.ttf> <書き出す.ttf> <リポジトリのフォルダー>

元のフォントと同じ SIL Open Font License 1.1 で提供します（ZenMaruGothic-OFL.txt）。
