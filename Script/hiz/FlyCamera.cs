using UnityEngine;
using UnityEngine.EventSystems;

public class FlyCamera : MonoBehaviour
{
    [Header("基础设置")]
    public float moveSpeed = 10f;
    public float sprintMultiplier = 3f;
    public float lookSensitivity = 2f;
    public float scrollSensitivity = 5f;

    [Header("移动端设置")]
    public float mobileLookSensitivity = 0.5f; // 手机触屏灵敏度通常需要低一点
    public float mobileMoveSensitivity = 0.05f;

    [Header("平滑处理")]
    public bool smoothMovement = true;
    public float smoothTime = 0.1f;

    private float _currentYaw;
    private float _currentPitch;
    private Vector3 _moveInput;
    private Vector3 _currentVelocity;

    // 移动端专用变量
    private Vector2 _leftTouchStartPos;
    private int _leftTouchId = -1;
    private int _rightTouchId = -1;

    void Start()
    {
        Vector3 angles = transform.eulerAngles;
        _currentYaw = angles.y;
        _currentPitch = angles.x;
    }

    void Update()
    {
        // 区分平台处理
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
        HandleMobileInputs();
#else
        HandleRotation();
        HandleMovement();
        HandleSpeedAdjustment();
#endif
    }

    #region PC Controls
    private void HandleRotation()
    {
        if (Input.GetMouseButton(1))
        {
            Cursor.lockState = CursorLockMode.Locked;
            float mouseX = Input.GetAxis("Mouse X") * lookSensitivity;
            float mouseY = Input.GetAxis("Mouse Y") * lookSensitivity;
            ApplyRotation(mouseX, mouseY);
        }
        else
        {
            Cursor.lockState = CursorLockMode.None;
        }
    }

    private void HandleMovement()
    {
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");
        float upDown = 0f;
        if (Input.GetKey(KeyCode.Space)) upDown = 1f;
        if (Input.GetKey(KeyCode.LeftControl)) upDown = -1f;

        Vector3 dir = (transform.forward * v + transform.right * h + transform.up * upDown).normalized;
        ApplyMovement(dir);
    }

    private void HandleSpeedAdjustment()
    {
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (scroll != 0)
        {
            moveSpeed = Mathf.Max(0.1f, moveSpeed + scroll * scrollSensitivity);
        }
    }
    #endregion

    #region Mobile Controls
    private void HandleMobileInputs()
    {
        for (int i = 0; i < Input.touchCount; i++)
        {
            Touch touch = Input.GetTouch(i);

            // 过滤 UI 点击（防止点 UI 时相机乱转）
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(touch.fingerId))
                continue;

            // 左半屏幕控制移动
            if (touch.position.x < Screen.width / 2)
            {
                if (touch.phase == TouchPhase.Began)
                {
                    _leftTouchId = touch.fingerId;
                    _leftTouchStartPos = touch.position;
                }
                else if (touch.phase == TouchPhase.Moved && touch.fingerId == _leftTouchId)
                {
                    Vector2 delta = touch.position - _leftTouchStartPos;
                    // 将触摸偏移转换为移动方向
                    Vector3 moveDir = (transform.forward * delta.y + transform.right * delta.x).normalized;
                    ApplyMovement(moveDir);
                }
                else if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
                {
                    _leftTouchId = -1;
                }
            }
            // 右半屏幕控制旋转
            else
            {
                if (touch.phase == TouchPhase.Began)
                {
                    _rightTouchId = touch.fingerId;
                }
                else if (touch.phase == TouchPhase.Moved && touch.fingerId == _rightTouchId)
                {
                    float mouseX = touch.deltaPosition.x * mobileLookSensitivity;
                    float mouseY = touch.deltaPosition.y * mobileLookSensitivity;
                    ApplyRotation(mouseX, mouseY);
                }
                else if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
                {
                    _rightTouchId = -1;
                }
            }
        }
    }
    #endregion

    #region Shared Logic
    private void ApplyRotation(float mouseX, float mouseY)
    {
        _currentYaw += mouseX;
        _currentPitch -= mouseY;
        _currentPitch = Mathf.Clamp(_currentPitch, -89f, 89f);
        transform.rotation = Quaternion.Euler(_currentPitch, _currentYaw, 0f);
    }

    private void ApplyMovement(Vector3 direction)
    {
        float multiplier = 1f;
#if !UNITY_ANDROID && !UNITY_IOS
        if (Input.GetKey(KeyCode.LeftShift)) multiplier = sprintMultiplier;
#endif

        Vector3 targetVelocity = direction * moveSpeed * multiplier;
        
        if (smoothMovement)
        {
            // 使用 SmoothDamp 平滑移动
            transform.position = Vector3.SmoothDamp(transform.position, transform.position + targetVelocity, ref _currentVelocity, smoothTime);
        }
        else
        {
            transform.position += targetVelocity * Time.deltaTime;
        }
    }
    #endregion

    // 安卓端升降建议：在场景中放两个 UI 按钮，调用这两个方法
    public void MobileMoveUp() { transform.position += Vector3.up * moveSpeed * Time.deltaTime; }
    public void MobileMoveDown() { transform.position += Vector3.down * moveSpeed * Time.deltaTime; }
}