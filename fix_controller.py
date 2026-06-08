f = 'src/Hbpos.Api/Controllers/LinklyController.cs'
with open(f, 'r', encoding='utf-8') as fp:
    c = fp.read()

# status-test: insert Exception catch after ValidationException catch
a1 = '        catch (LinklyCloudBackendValidationException ex)\n        {\n            return BadRequest(ApiResult<LinklyCloudBackendStatusTestResponse>.Fail(\n                CloudBackendInvalidCode,\n                ex.Message));\n        }\n    }\n\n    [HttpPost("cloud-backend/logon-test")]'

r1 = '        catch (LinklyCloudBackendValidationException ex)\n        {\n            return BadRequest(ApiResult<LinklyCloudBackendStatusTestResponse>.Fail(\n                CloudBackendInvalidCode,\n                ex.Message));\n        }\n        catch (Exception ex)\n        {\n            Log($"cloud-backend status-test error={ex.GetType().Name} message={ex.Message}");\n            return StatusCode(\n                StatusCodes.Status500InternalServerError,\n                ApiResult<LinklyCloudBackendStatusTestResponse>.Fail(\n                    CloudBackendFailedCode,\n                    "An unexpected error occurred."));\n        }\n    }\n\n    [HttpPost("cloud-backend/logon-test")]'

if a1 in c:
    c = c.replace(a1, r1)
    print('status-test: replaced')
else:
    a1_crlf = a1.replace('\n', '\r\n')
    if a1_crlf in c:
        c = c.replace(a1_crlf, r1.replace('\n', '\r\n'))
        print('status-test: replaced (CRLF)')
    else:
        print('status-test: NOT FOUND')

a2 = '        catch (LinklyCloudBackendValidationException ex)\n        {\n            return BadRequest(ApiResult<LinklyCloudBackendLogonTestResponse>.Fail(\n                CloudBackendInvalidCode,\n                ex.Message));\n        }\n    }\n\n    [HttpGet("cloud-backend/transactions/{sessionId}/status")]'

r2 = '        catch (LinklyCloudBackendValidationException ex)\n        {\n            return BadRequest(ApiResult<LinklyCloudBackendLogonTestResponse>.Fail(\n                CloudBackendInvalidCode,\n                ex.Message));\n        }\n        catch (Exception ex)\n        {\n            Log($"cloud-backend logon-test error={ex.GetType().Name} message={ex.Message}");\n            return StatusCode(\n                StatusCodes.Status500InternalServerError,\n                ApiResult<LinklyCloudBackendLogonTestResponse>.Fail(\n                    CloudBackendFailedCode,\n                    "An unexpected error occurred."));\n        }\n    }\n\n    [HttpGet("cloud-backend/transactions/{sessionId}/status")]'

if a2 in c:
    c = c.replace(a2, r2)
    print('logon-test: replaced')
else:
    a2_crlf = a2.replace('\n', '\r\n')
    if a2_crlf in c:
        c = c.replace(a2_crlf, r2.replace('\n', '\r\n'))
        print('logon-test: replaced (CRLF)')
    else:
        print('logon-test: NOT FOUND')

with open(f, 'w', encoding='utf-8') as fp:
    fp.write(c)
print('write done')
