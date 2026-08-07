# Pack index v2 large offsets

Pack index v2 stores offsets below 2 GiB directly in its 32-bit offset table. When an entry's
high bit is set, the remaining 31 bits select an unsigned 64-bit big-endian value from the
large-offset table. GitCore previously rejected these entries.

`pack-index-v2-large-offsets.idx` is a 1,264-byte synthetic fixture derived from the Git-generated
index for the existing six-object test pack. Three entries were changed to reference the
large-offset table, the table was populated with offsets above 4 GiB, and the trailing index
SHA-1 was recomputed. The fixture retains three direct offsets so both encodings are covered.

The file was verified with Git 2.54.0:

```sh
git show-index < pack-index-v2-large-offsets.idx
```

Git parsed the index and printed:

```text
4294967308 14eb05f5beac67cdf2a229394baa626338a3d92e (c027697e)
3246 3fc9c379df421c0ff1ea909313511f704b4c8e27 (344f3327)
8589937183 565ed90978eb8a077e87ebaf583a9efd74afdeb1 (de5b9555)
2545 59e076f24327dd722c560107150c1f17cd715306 (c230ab3b)
12884902052 8ba2247ab0a7fca6750be46db85f80344ae0df44 (680a5e72)
318 f1d9a5bbe103d120903d51a0c3f615ec8135c4ce (4ebe6d6f)
```
