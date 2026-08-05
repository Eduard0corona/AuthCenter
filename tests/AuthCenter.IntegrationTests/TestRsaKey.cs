namespace AuthCenter.IntegrationTests;

/// <summary>
/// Signing key used only by the automated tests. It is generated for this purpose and must never
/// be reused by a deployed environment — anything committed here is public.
/// </summary>
public static class TestRsaKey
{
    public const string PrivateKeyPem = """
        -----BEGIN PRIVATE KEY-----
        MIIEvgIBADANBgkqhkiG9w0BAQEFAASCBKgwggSkAgEAAoIBAQDsyBWWZbSCwgoQ
        1BgtcubunOJ6k9MLUBxl8ZCR1Mj5+o5FlPDXVE1jxNVXplSoRP6FOlT1bhZol3QZ
        veIo5p66vI9jNjPvTMqePXpVz7A0d7bzGGRJyVNy4jfQ5D3l32sLu4vNR3wur90U
        57CY7VdKFbzC6wHBJe9m2+y24hwsphksnbRqR/OXS8SG4amv1lkETrkmCCudaFNJ
        CGPVD5pZF8I/Fcza5sEd8eEoVtMs9SwMapKOSGkfq4xuKQmCmMazUoOjIGBsJCXn
        jGUdw1X1oUNBo5VwoKuV3pHW0/AETxNcHWa5ybql3cY3jhPJTJ/pq89zrr52kZtm
        Fjyaa+29AgMBAAECggEAAWL95RIJbViFrPpjT0jj+c5y38yFwKcgCaILd5qv1STz
        WEawQnxko9iowCLNm7YJAH8evQciWQXPj4jSHMJYVWeG6t9Zdb3V8R+W0habApwE
        Hcux9po1DFnllM0QTUhrpCMjcdUG4WehbVnghwRi8KCA+mZvvUMa8DTrDEIzyN8s
        sSp/F6OyOUZFNz/EVG3JF6VfAuxLwoW8ZHm4oMRT4BtTpRn35qMEMnwaQu5o8Oqx
        l9nJMBEkiqzJ0wHQx8A5l33TujYNKPSu4jPWeuUeSlM3/JXRllmUmaeesmXBhGDR
        mkP9blFT6m9MwKBpcqQkqxyvSufwvGQ1/28xqlQa6QKBgQD9mMBS+p4daTGu8c3h
        rvi6vRFg1SqNchvX4Wg0b65Qn+elYxWTw5oz+kGIfnC+Pywwf2sexGq998cmysNU
        PPdiiPOPZZRg+kdXEwsEWtKLQRfmdQmnXxCf5jnFtQ3O5+A5jIX1oJVr7LypEk3y
        BcmIgGSX+vT6tVXQRXHKAo6G2QKBgQDvBom/eQf/4wnbAU8PmaB1MpeXt3lZuzIf
        Kvl9DajGGJ2KETL1ZYi+AEjfow8UvHgIMvfq+ckM9Phh1Va90A8Qv2rwt+A57vfc
        2Px6heU5IXkhqx+mCCIPXABOGTxM1MJ13fN6NgK9ETNm8CKMJ4+zCPRxAppclCQF
        siblHpl3hQKBgQDLW9TONd7ZYuPOkGUy1ybqSHdJGWeYKaseQMlZUz2Ltc8ClyRU
        bASaLdKjbBFo3ivHcEYsVAHs+GchOnPMd19CCuSVVzXXVpwivUiWY2Z2+On7ilsF
        dnxUXUByxK+f/d2XCJLb17w64YqgYedTb0SfS6ZfIYWWDhgUfbuz7LzycQKBgE0E
        gUwV9pLoGBvePxhauw4oYBo9vzc/jzXlxgAab6Bs/A4p3o6dycLXGqQcyVY4KEEU
        Ezg+hh7LrGDxugJtUP1ngFDSHsjsDCe4LxpXnHWdKIfFuOvpwPfMADkp/nkPMR2D
        h9mAH7/GFBb+F1Orx6y7nO/xE8Xy4eyH6+p1wqN1AoGBAIt+LDa/YUyipAPJrI6z
        9DNkWcXhmxLuQDfZ3CYkyl4t18snffB9iIeY8zjLQs9EraYFl1skd1hAbFz1pcic
        4vUtt2y6Jsegw7c0j8/Juy4Zr442WwVMOWHyUV83NI1Bplti2ZQXqW+sYc0XInAo
        B8JgtYUsG8m3lLPjeudctsNR
        -----END PRIVATE KEY-----
        """;
}
