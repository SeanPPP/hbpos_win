import sys
f = 'src/Hbpos.Api/Services/LinklyCloudBackendAsyncService.cs'
with open(f, 'r', encoding='utf-8') as fp:
    c = fp.read()
if 'using static Hbpos.Contracts.Linkly.LinklyCloudBackendStatusConstants;' not in c:
    c = c.replace('using System.Globalization;\nusing System.Diagnostics;\n', 'using System.Globalization;\nusing System.Diagnostics;\nusing static Hbpos.Contracts.Linkly.LinklyCloudBackendStatusConstants;\n')
    print('added using static')
consts = [
    '    private const string StatusPending = "Pending";',
    '    private const string StatusCompleted = "Completed";',
    '    private const string StatusNotSubmitted = "NotSubmitted";',
    '    private const string StatusTokenRefreshRequired = "TokenRefreshRequired";',
    '    private const string StatusFailed = "Failed";',
    '    private const string RecoveryRetry = "Retry";',
    '    private const string RecoveryRefreshToken = "RefreshToken";',
]
for cl in consts:
    if cl + '\n' in c:
        c = c.replace(cl + '\n', '')
        print('removed:', cl)
with open(f, 'w', encoding='utf-8') as fp:
    fp.write(c)
print('done')
