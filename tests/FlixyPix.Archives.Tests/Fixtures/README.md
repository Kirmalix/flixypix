# RAR decoder reference fixtures

These files are copied verbatim from the MIT-licensed SharpCompress repository,
commit `b6cc95af73950c914e0c6ce7ea3511528ede1121` (the NuGet 1.0.0 source commit):

https://github.com/adamhathcock/sharpcompress/tree/b6cc95af73950c914e0c6ce7ea3511528ede1121/tests/TestArchives/Archives

The accompanying upstream license is `SharpCompress-LICENSE.txt`. These are third-party
test data, not FlixyPix original files or personal user media. The archives contain
upstream test members including a JPEG, a text file and an executable. The tests never
execute or publish an extracted executable: only the JPEG is extracted into a temporary
cache. Solid decoding necessarily reads preceding members as byte streams.

| File | SHA-256 |
|---|---|
| Rar4.rar | `9d9c261e50d3a84ab11a3701c7a736057fc970e59ca365885a41f60270c1875e` |
| Rar.solid.rar | `e7e62f24f22f195d21437be09667c8956837d99ba48d7270090251a8e32babc5` |
| Rar5.rar | `3319de3e8a91a58d08d8a83f48ebae7e6b022c542b4a58775bab64f4063988a9` |
| Rar5.solid.rar | `3043d68c5a81f46adcbb5393632d243944ead44eb383a677ac82e68afdde27cf` |
| Rar5.encrypted_filesOnly.rar | `e5fb06af812633b246430f029a051cc462d00180a0df455a798644deccdc7d07` |
| Rar5.multi.part01.rar | `ad9b6ac157e77ba2cc6b8be7eeba52a2c047a0ef2d06fa6ffa0c1d1ccce3a651` |

The expected JPEG has 40,372 bytes and SHA-256
`b251c7501fb0f55dd4a92feabe0a6f5733bc40a02679498155fae9b30138fc53`,
matching the upstream `tests/TestArchives/Original/jpg/test.jpg`.

ZIP64 fixtures are generated entirely by the tests from four synthetic bytes;
they require no external archives or network access.
